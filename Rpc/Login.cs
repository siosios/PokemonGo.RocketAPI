﻿#region using directives

using System;
using System.Threading.Tasks;
using PokemonGo.RocketAPI.Enums;
using PokemonGo.RocketAPI.Exceptions;
using PokemonGo.RocketAPI.Helpers;
using POGOProtos.Networking.Responses;
using System.IO;
using Newtonsoft.Json;
using System.Threading;
using PokemonGo.RocketAPI.LoginProviders;
using PokemonGo.RocketAPI.Authentication.Data;
using PokemonGo.RocketAPI.Authentication;
using POGOProtos.Networking.Envelopes;

#endregion

namespace PokemonGo.RocketAPI.Rpc
{
    public delegate void GoogleDeviceCodeDelegate(string code, string uri);

    public class Login : BaseRpc
    {
        private static Semaphore ReauthenticateMutex { get; } = new Semaphore(1, 1);
        public Login(Client client) : base(client)
        {
            Client.LoginProvider = SetLoginType(client.Settings);
            Client.ApiUrl = Resources.RpcUrl;
        }

        private static ILoginProvider SetLoginType(ISettings settings)
        {
            switch (settings.AuthType)
            {
                case AuthType.Google:
                    return new GoogleLoginProvider(settings.GoogleUsername, settings.GooglePassword);
                case AuthType.Ptc:
                    return new PtcLoginProvider(settings.PtcUsername, settings.PtcPassword);
                default:
                    throw new ArgumentOutOfRangeException(nameof(settings.AuthType), "Unknown AuthType");
            }
        }

        public static void SetAuthTicketOnAccessToken(Client client, AuthTicket authTicket)
        {
            try
            {
                ReauthenticateMutex.WaitOne();
                if (client.AccessToken != null)
                    client.AccessToken.AuthTicket = authTicket;
            }
            finally
            {
                ReauthenticateMutex.Release();
            }
        }

        private static bool IsValidAccessToken(AccessToken accessToken)
        {
            if (accessToken == null)
                return false;

            // If auth ticket is not null, check auth ticket expiration (with 10 minute buffer)
            if (accessToken.AuthTicket != null && accessToken.AuthTicket.ExpireTimestampMs > (ulong)Utils.GetTime(true) - (60000 * 10))
            {
                // If about to expire, then null out the auth ticket.
                accessToken.AuthTicket = null;  
            }

            if (accessToken.AuthTicket != null)
            {
                // If we have auth ticket not about to expire, then return true.
                return true;
            }

            // No auth ticket, so check if access token is valid.
            if (string.IsNullOrEmpty(accessToken.Token) || accessToken.IsExpired)
                return false;
            
            // If we got here, then we have a valid non-expired access token.
            return true;
        }

        private static bool IsValidAuthTicket(AccessToken accessToken)
        {
            return accessToken != null && accessToken.AuthTicket != null && accessToken.AuthTicket.ExpireTimestampMs < (ulong)Utils.GetTime(true) - (60000 * 10);
        }

        public static async Task<AccessToken> GetValidAccessToken(Client client, bool forceRefresh = false, bool isCached = false)
        {
            try
            {
                ReauthenticateMutex.WaitOne();

                if (forceRefresh)
                {
                    client.AccessToken.Expire();
                    if (isCached)
                        DeleteSavedAccessToken(client);
                }

                if (IsValidAccessToken(client.AccessToken))
                    return client.AccessToken;
                
                // If we got here then access token is expired or not loaded into memory.
                if (isCached)
                {
                    var loginProvider = client.LoginProvider;
                    var cacheDir = Path.Combine(Directory.GetCurrentDirectory(), "Cache");
                    var fileName = Path.Combine(cacheDir, $"{loginProvider.UserId}-{loginProvider.ProviderId}.json");

                    if (!Directory.Exists(cacheDir))
                        Directory.CreateDirectory(cacheDir);

                    if (File.Exists(fileName))
                    {
                        var accessToken = JsonConvert.DeserializeObject<AccessToken>(File.ReadAllText(fileName));

                        if (!accessToken.IsExpired)
                        {
                            client.AccessToken = accessToken;
                            return accessToken;
                        }
                    }
                }

                await Reauthenticate(client, isCached);
                return client.AccessToken;
            }
            finally
            {
                ReauthenticateMutex.Release();
            }
        }

        private static void SaveAccessToken(AccessToken accessToken)
        {
            if (accessToken == null || string.IsNullOrEmpty(accessToken.Uid) || string.IsNullOrEmpty(accessToken.Token) || accessToken.IsExpired)
                return;

            var fileName = Path.Combine(Directory.GetCurrentDirectory(), "Cache", $"{accessToken.Uid}.json");

            File.WriteAllText(fileName, JsonConvert.SerializeObject(accessToken, Formatting.Indented));
        }

        private static void DeleteSavedAccessToken(Client client)
        {
            var cacheDir = Path.Combine(Directory.GetCurrentDirectory(), "Cache");
            var fileName = Path.Combine(cacheDir, $"{client.AccessToken?.Uid}-{client.LoginProvider.ProviderId}.json");
            if (File.Exists(fileName))
                File.Delete(fileName);
        }

        private static async Task Reauthenticate(Client client, bool isCached)
        {
            var tries = 0;
            while (null == client.AccessToken || client.AccessToken.IsExpired || string.IsNullOrEmpty(client.AccessToken.Token))
            {
                // If expired, then we always delete the saved access token if it exists.
                if (isCached)
                    DeleteSavedAccessToken(client);

                try
                {
                    client.AccessToken = await client.LoginProvider.GetAccessToken();
                }
                catch (Exception ex)
                {

                    Console.WriteLine(ex.Message);
                    if (ex.Message.Contains("You have to log into an browser")) throw new GoogleTwoFactorException(ex.Message);
                    //Logger.Error($"Reauthenticate exception was catched: {exception}");
                }
                finally
                {
                    if (null == client.AccessToken || client.AccessToken.IsExpired || string.IsNullOrEmpty(client.AccessToken.Token))
                    {
                        var sleepSeconds = Math.Min(60, ++tries * 5);
                        //Logger.Error($"Reauthentication failed, trying again in {sleepSeconds} seconds.");
                        await Task.Delay(TimeSpan.FromMilliseconds(sleepSeconds * 1000));
                    }
                    else
                    {
                        // We have successfully refreshed the token so save it.
                        if (isCached)
                            SaveAccessToken(client.AccessToken);
                    }

                    if (tries == 5)
                    {
                        throw new TokenRefreshException("Error refreshing access token.");
                    }
                }
            }
        }

        public async Task<GetPlayerResponse> DoLogin()
        {
            // Don't wait for background start of killswitch.
            // jjskuld - Ignore CS4014 warning for now.
#pragma warning disable 4014
            Client.KillswitchTask.Start();
#pragma warning restore 4014

            Client.StartTime = Utils.GetTime(true);
            Client.RequestBuilder = new RequestBuilder(Client, Client.Settings);

            var player = await Client.Player.GetPlayer(false); // Set false because initial GetPlayer does not use common requests.

            await Client.Download.GetRemoteConfigVersion();
            await Client.Download.GetAssetDigest();
            await Client.Download.GetItemTemplates();

            await Client.Player.GetPlayerProfile();

            return player;
        }
    }
}