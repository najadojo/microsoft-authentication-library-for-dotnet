﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using AndroidNative = Android;
using Microsoft.Identity.Client.Core;
using Microsoft.Identity.Client.Internal.Broker;
using Microsoft.Identity.Client.OAuth2;
using Microsoft.Identity.Client.UI;
using Microsoft.Identity.Json.Linq;
using Microsoft.Identity.Client.Internal.Requests;
using Microsoft.Identity.Client.ApiConfig.Parameters;
using Microsoft.Identity.Client.Http;
using System.Net;
using Android.OS;
using System.Linq;
using Android.Accounts;
using Java.Util.Concurrent;

namespace Microsoft.Identity.Client.Platforms.Android.Broker
{
    [AndroidNative.Runtime.Preserve(AllMembers = true)]
    internal class AndroidBroker : IBroker
    {

        private static MsalTokenResponse s_androidBrokerTokenResponse = null;
        //Since the correlation ID is not returned from the broker response, it must be stored at the beginning of the authentication call and re-injected into the response at the end.
        private static string s_correlationId;
        private readonly AndroidBrokerHelper _brokerHelper;
        private readonly ICoreLogger _logger;
        private readonly Activity _parentActivity;

        public AndroidBroker(CoreUIParent uiParent, ICoreLogger logger)
        {
            _parentActivity = uiParent?.Activity;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            AuthenticationContinuationHelper.LastRequestLogger = _logger;
            _brokerHelper = new AndroidBrokerHelper(Application.Context, logger);
        }

        public bool IsBrokerInstalledAndInvokable()
        {
            using (_logger.LogMethodDuration())
            {
                bool canInvoke = _brokerHelper.CanSwitchToBroker();
                _logger.Verbose("Can invoke broker? " + canInvoke);

                return canInvoke;
            }
        }

        ///Check if the network is available.
        private void CheckPowerOptimizationStatus()
        {
            checkPackageForPowerOptimization(Application.Context.PackageName);
            checkPackageForPowerOptimization(_brokerHelper.Authenticators.FirstOrDefault().PackageName);
        }

        private void checkPackageForPowerOptimization(string package)
        {
            var powerManager = PowerManager.FromContext(Application.Context);

            //Power optimization checking was added in API 23
            if ((int)Build.VERSION.SdkInt >= (int)BuildVersionCodes.M &&
                powerManager.IsDeviceIdleMode &&
                !powerManager.IsIgnoringBatteryOptimizations(package))
            {
                _logger.Error("Power optimization detected for the application: " + package + " and the device is in doze mode or the app is in standby. \n" +
                    "Please disable power optimizations for this application to authenticate.");
            }
        }

        public async Task<MsalTokenResponse> AcquireTokenInteractiveAsync(
            AuthenticationRequestParameters authenticationRequestParameters,
            AcquireTokenInteractiveParameters acquireTokenInteractiveParameters)
        {

            CheckPowerOptimizationStatus();

            s_androidBrokerTokenResponse = null;

            BrokerRequest brokerRequest = BrokerRequest.FromInteractiveParameters(
                authenticationRequestParameters, acquireTokenInteractiveParameters);

            // There can only be 1 broker request at a time so keep track of the correlation id
            s_correlationId = brokerRequest.CorrelationId;

            try
            {
                await InitiateBrokerHandshakeAsync(_parentActivity).ConfigureAwait(false);
                await AcquireTokenInteractiveViaBrokerAsync(brokerRequest).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.ErrorPiiWithPrefix(ex, "Android broker interactive invocation failed. ");
                HandleBrokerOperationError(ex);
            }

            using (_logger.LogBlockDuration("Waiting for Android broker response. "))
            {
                await AndroidBrokerHelper.ReadyForResponse.WaitAsync().ConfigureAwait(false);
                return s_androidBrokerTokenResponse;
            }
        }

        public async Task<MsalTokenResponse> AcquireTokenSilentAsync(
            AuthenticationRequestParameters authenticationRequestParameters,
            AcquireTokenSilentParameters acquireTokenSilentParameters)
        {
            CheckPowerOptimizationStatus();

            BrokerRequest brokerRequest = BrokerRequest.FromSilentParameters(
                authenticationRequestParameters, acquireTokenSilentParameters);

            try
            {
                await InitiateBrokerHandshakeAsync(_parentActivity).ConfigureAwait(false);
                var androidBrokerTokenResponse = await AcquireTokenSilentViaBrokerAsync(brokerRequest).ConfigureAwait(false);
                return androidBrokerTokenResponse;
            }
            catch (Exception ex)
            {
                _logger.ErrorPiiWithPrefix(ex, "Android broker silent invocation failed. ");
                HandleBrokerOperationError(ex);
                throw;
            }
        }

        private async Task AcquireTokenInteractiveViaBrokerAsync(BrokerRequest brokerRequest)
        {
            using (_logger.LogMethodDuration())
            {
                // onActivityResult will receive the response for this activity.
                // Launching this activity will switch to the broker app.

                _logger.Verbose("Starting Android Broker interactive authentication. ");
                Intent brokerIntent = await _brokerHelper
                    .GetIntentForInteractiveBrokerRequestAsync(brokerRequest, _parentActivity)
                    .ConfigureAwait(false);

                if (brokerIntent != null)
                {
                    try
                    {
                        _logger.Info(
                            "Calling activity pid:" + AndroidNative.OS.Process.MyPid()
                            + " tid:" + AndroidNative.OS.Process.MyTid() + "uid:"
                            + AndroidNative.OS.Process.MyUid());

                        _parentActivity.StartActivityForResult(brokerIntent, 1001);
                    }
                    catch (ActivityNotFoundException e)
                    {
                        _logger.ErrorPiiWithPrefix(e, "Unable to get Android activity during interactive broker request. ");
                        throw;
                    }
                }
            }
        }

        private async Task<MsalTokenResponse> AcquireTokenSilentViaBrokerAsync(BrokerRequest brokerRequest)
        {
            // Don't send silent background request if account information is not provided

            using (_logger.LogMethodDuration())
            {
                _logger.Verbose("User is specified for silent token request. Starting silent Android broker request. ");
                string silentResult = await _brokerHelper.GetBrokerAuthTokenSilentlyAsync(brokerRequest, _parentActivity).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(silentResult))
                {
                    return MsalTokenResponse.CreateFromAndroidBrokerResponse(silentResult, brokerRequest.CorrelationId);
                }

                return new MsalTokenResponse
                {
                    Error = MsalError.BrokerResponseReturnedError,
                    ErrorDescription = "Unknown Android broker error. Failed to acquire token silently from the broker. " + MsalErrorMessage.AndroidBrokerCannotBeInvoked,
                };
            }
        }

        public void HandleInstallUrl(string appLink)
        {
            _logger.Info("Android Broker - Starting ActionView activity to " + appLink);
            _parentActivity.StartActivity(new Intent(Intent.ActionView, AndroidNative.Net.Uri.Parse(appLink)));

            throw new MsalClientException(
                MsalError.BrokerApplicationRequired,
                MsalErrorMessage.BrokerApplicationRequired);
        }

        public async Task<IEnumerable<IAccount>> GetAccountsAsync(string clientID, string redirectUri)
        {
            using (_logger.LogMethodDuration())
            {
                if (!IsBrokerInstalledAndInvokable())
                {
                    _logger.Warning("Android broker is either not installed or is not reachable so no accounts will be returned. ");
                    return new List<IAccount>();
                }

                BrokerRequest brokerRequest = new BrokerRequest() { ClientId = clientID, RedirectUri = new Uri(redirectUri) };

                try
                {
                    await InitiateBrokerHandshakeAsync(_parentActivity).ConfigureAwait(false);

                    return _brokerHelper.GetBrokerAccountsInAccountManager(brokerRequest);
                }
                catch (Exception ex)
                {
                    _logger.Error("Failed to get Android broker accounts from the broker. ");
                    HandleBrokerOperationError(ex);
                    throw;
                }
            }
        }

        public async Task RemoveAccountAsync(IApplicationConfiguration applicationConfiguration, IAccount account)
        {
            using (_logger.LogMethodDuration())
            {
                if (!IsBrokerInstalledAndInvokable())
                {
                    _logger.Warning("Android broker is either not installed or not reachable so no accounts will be removed. ");
                    return;
                }

                try
                {
                    await InitiateBrokerHandshakeAsync(_parentActivity).ConfigureAwait(false);
                    _brokerHelper.RemoveBrokerAccountInAccountManager(applicationConfiguration.ClientId, account);
                }
                catch (Exception ex)
                {
                    _logger.Error("Failed to remove Android broker account from the broker. ");
                    HandleBrokerOperationError(ex);
                    throw;
                }
            }
        }

        //In order for broker to use the V2 endpoint during authentication, MSAL must initiate a handshake with broker to specify what endpoint should be used for the request.
        public async Task InitiateBrokerHandshakeAsync(Activity callerActivity)
        {
            using (_logger.LogMethodDuration())
            {
                try
                {
                    Bundle helloRequestBundle = new Bundle();
                    helloRequestBundle.PutString(BrokerConstants.ClientAdvertisedMaximumBPVersionKey, BrokerConstants.BrokerProtocalVersionCode);
                    helloRequestBundle.PutString(BrokerConstants.ClientConfiguredMinimumBPVersionKey, "2.0");
                    helloRequestBundle.PutString(BrokerConstants.BrokerAccountManagerOperationKey, "HELLO");

                    IAccountManagerFuture result = _brokerHelper.AndroidAccountManager.AddAccount(BrokerConstants.BrokerAccountType,
                        BrokerConstants.AuthtokenType,
                        null,
                        helloRequestBundle,
                        null,
                        null,
                        _brokerHelper.GetPreferredLooper(callerActivity));

                    if (result != null)
                    {
                        Bundle bundleResult = null;

                        try
                        {
                            bundleResult = (Bundle)await result.GetResultAsync(
                                _brokerHelper.AccountManagerTimeoutSeconds,
                                TimeUnit.Seconds)
                                .ConfigureAwait(false);
                        }
                        catch (System.OperationCanceledException ex)
                        {
                            _logger.Error("An error occurred when trying to communicate with the account manager: " + ex.Message);
                        }

                        var bpKey = bundleResult?.GetString(BrokerConstants.NegotiatedBPVersionKey);

                        if (!string.IsNullOrEmpty(bpKey))
                        {
                            _logger.Info("Using broker protocol version: " + bpKey);
                            return;
                        }

                        dynamic errorResult = JObject.Parse(bundleResult?.GetString(BrokerConstants.BrokerResultV2));
                        string errorCode = null;
                        string errorDescription = null;

                        if (!string.IsNullOrEmpty(errorResult))
                        {
                            errorCode = errorResult[BrokerResponseConst.BrokerErrorCode]?.ToString();
                            string errorMessage = errorResult[BrokerResponseConst.BrokerErrorMessage]?.ToString();
                            errorDescription = $"An error occurred during hand shake with the broker. Error: {errorCode} Error Message: {errorMessage}";
                        }
                        else
                        {
                            errorCode = BrokerConstants.BrokerUnknownErrorCode;
                            errorDescription = "An error occurred during hand shake with the broker, no detailed error information was returned. ";
                        }

                        _logger.Error(errorDescription);
                        throw new MsalClientException(errorCode, errorDescription);
                    }

                    throw new MsalClientException("Could not communicate with broker via account manager. Please ensure power optimization settings are turned off. ");
                }
                catch (Exception ex)
                {
                    _logger.Error("Error when trying to initiate communication with the broker. ");
                    if (ex is MsalException)
                    {
                        throw;
                    }

                    throw new MsalClientException(MsalError.BrokerApplicationRequired, MsalErrorMessage.AndroidBrokerCannotBeInvoked, ex);
                }
            }
        }

        private void HandleBrokerOperationError(Exception ex)
        {
            _logger.Error(ex.Message);
            if (ex is MsalException)
                throw ex;
            else
                throw new MsalClientException(MsalError.AndroidBrokerOperationFailed, ex.Message, ex);
        }

        /// <summary>
        /// Android Broker does not support logging in a "default" user.
        /// </summary>
        public Task<MsalTokenResponse> AcquireTokenSilentDefaultUserAsync(
            AuthenticationRequestParameters authenticationRequestParameters,
            AcquireTokenSilentParameters acquireTokenSilentParameters)
        {
            throw new MsalUiRequiredException(
                       MsalError.CurrentBrokerAccount,
                       MsalErrorMessage.MsalUiRequiredMessage,
                       null,
                       UiRequiredExceptionClassification.AcquireTokenSilentFailed);
        }
    }
}
