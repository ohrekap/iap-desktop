﻿//
// Copyright 2023 Google LLC
//
// Licensed to the Apache Software Foundation (ASF) under one
// or more contributor license agreements.  See the NOTICE file
// distributed with this work for additional information
// regarding copyright ownership.  The ASF licenses this file
// to you under the Apache License, Version 2.0 (the
// "License"); you may not use this file except in compliance
// with the License.  You may obtain a copy of the License at
// 
//   http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing,
// software distributed under the License is distributed on an
// "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY
// KIND, either express or implied.  See the License for the
// specific language governing permissions and limitations
// under the License.
//

using Google.Apis.Auth.OAuth2;
using Google.Apis.Http;
using Google.Apis.Services;
using Google.Solutions.Apis.Auth;
using Google.Solutions.Common.Util;
using System.Diagnostics;
using System.Net.Http;
using System.Threading.Tasks;
using System.Threading;
using Google.Apis.Auth.OAuth2.Flows;
using System;

namespace Google.Solutions.Apis.Client
{
    internal static class Initializers 
    {
        /// <summary>
        /// Create an initializer for API services that configures PSC
        /// and mTLS.
        /// </summary>
        public static BaseClientService.Initializer CreateServiceInitializer(
            IServiceEndpoint endpoint,
            IAuthorization authorization,
            UserAgent userAgent)
        {
            Precondition.ExpectNotNull(endpoint, nameof(endpoint));
            Precondition.ExpectNotNull(authorization, nameof(authorization));
            Precondition.ExpectNotNull(userAgent, nameof(userAgent));

            var directions = endpoint.GetDirections(
                authorization.DeviceEnrollment?.State ?? DeviceEnrollmentState.NotEnrolled);

            ApiTraceSources.Default.TraceInformation(
                "Using endpoint {0}",
                directions);

            return new BaseClientService.Initializer()
            {
                BaseUri = directions.BaseUri.ToString(),
                ApplicationName = userAgent.ToApplicationName(),
                HttpClientFactory = new PscAndMtlsAwareHttpClientFactory(
                    directions,
                    authorization)
            };
        }

        /// <summary>
        /// Create an initializer for OAuth that configures mTLS.
        /// </summary>
        public static OpenIdInitializer CreateOpenIdInitializer(
            ServiceEndpoint<SignInClient.OAuthClient> oauthEndpoint,
            ServiceEndpoint<SignInClient.OpenIdClient> openIdEndpoint,
            IDeviceEnrollment enrollment)
        {
            Precondition.ExpectNotNull(oauthEndpoint, nameof(oauthEndpoint));
            Precondition.ExpectNotNull(openIdEndpoint, nameof(openIdEndpoint));
            Precondition.ExpectNotNull(enrollment, nameof(enrollment));

            var oauthDirections = oauthEndpoint.GetDirections(
                enrollment?.State ?? DeviceEnrollmentState.NotEnrolled);

            var openIdDirections = openIdEndpoint.GetDirections(
                enrollment?.State ?? DeviceEnrollmentState.NotEnrolled);

            ApiTraceSources.Default.TraceInformation(
                "OAuth: Using endpoint {0}",
                oauthDirections);
            ApiTraceSources.Default.TraceInformation(
                "OpenID: Using endpoint {0}",
                openIdDirections);

            //
            // NB. OidcAuthorizationUrl is a browser endpoint, not an
            // API endpoint. Therefore, it's not subject to the 
            // service endpoint logic.
            //

            return new OpenIdInitializer(
                new Uri(GoogleAuthConsts.OidcAuthorizationUrl),
                new Uri(oauthDirections.BaseUri, "/token"),
                new Uri(oauthDirections.BaseUri, "/revoke"),
                new Uri(openIdDirections.BaseUri, "/v1/userinfo"))
            {
                HttpClientFactory = new MtlsAwareHttpClientFactory(
                    oauthDirections,
                    enrollment)
            };
        }

        //---------------------------------------------------------------------
        // Inner classes.
        //---------------------------------------------------------------------

        public class OpenIdInitializer : GoogleAuthorizationCodeFlow.Initializer
        {
            public Uri UserInfoUrl { get; }

            public OpenIdInitializer(
                Uri authorizationServerUrl, 
                Uri tokenServerUrl,
                Uri revokeTokenUrl,
                Uri userInfoUrl) 
                : base(
                      authorizationServerUrl.ToString(),
                      tokenServerUrl.ToString(), 
                      revokeTokenUrl.ToString())
            {
                this.UserInfoUrl = userInfoUrl;
            }
        }

        /// <summary>
        /// Client factory that enables client certificate and adds PSC-style Host headers
        /// if needed.
        /// </summary>
        private class PscAndMtlsAwareHttpClientFactory : IHttpClientFactory, IHttpExecuteInterceptor
        {
            private readonly ServiceEndpointDirections directions;
            private readonly IDeviceEnrollment deviceEnrollment;
            private readonly ICredential credential;

            public PscAndMtlsAwareHttpClientFactory(
                ServiceEndpointDirections directions,
                IAuthorization authorization)
            {
                this.directions = directions;
                this.deviceEnrollment = authorization.DeviceEnrollment;
                this.credential = authorization.Credential;
            }

            public PscAndMtlsAwareHttpClientFactory(
                ServiceEndpointDirections directions,
                IDeviceEnrollment deviceEnrollment)
            {
                this.directions = directions;
                this.deviceEnrollment = deviceEnrollment;
                this.credential = null;
            }

            public ConfigurableHttpClient CreateHttpClient(CreateHttpClientArgs args)
            {
                if (this.credential != null)
                {
                    args.Initializers.Add(this.credential);
                }

                var factory = new MtlsAwareHttpClientFactory(this.directions, this.deviceEnrollment);
                var httpClient = factory.CreateHttpClient(args);

                httpClient.MessageHandler.AddExecuteInterceptor(this);

                return httpClient;
            }

            public Task InterceptAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                if (this.directions.Type == ServiceEndpointType.PrivateServiceConnect)
                {
                    Debug.Assert(!string.IsNullOrEmpty(this.directions.Host));

                    //
                    // We're using PSC, thw so hostname we're using to connect is
                    // different than what the server expects.
                    //
                    Debug.Assert(request.RequestUri.Host != this.directions.Host);

                    //
                    // Inject the normal hostname so that certificate validation works.
                    //
                    request.Headers.Host = this.directions.Host;
                }

                return Task.CompletedTask;
            }
        }

        /// <summary>
        /// Client factory that enables client certificate authenticateion
        /// if the device is enrolled.
        /// </summary>
        private class MtlsAwareHttpClientFactory : HttpClientFactory
        {
            private readonly ServiceEndpointDirections directions;
            private readonly IDeviceEnrollment deviceEnrollment;

            public MtlsAwareHttpClientFactory(
                ServiceEndpointDirections directions,
                IDeviceEnrollment deviceEnrollment)
            {
                this.directions = directions.ExpectNotNull(nameof(directions));
                this.deviceEnrollment = deviceEnrollment.ExpectNotNull(nameof(deviceEnrollment));
            }

            protected override HttpClientHandler CreateClientHandler()
            {
                var handler = base.CreateClientHandler();

                if (this.directions.UseClientCertificate &&
                    HttpClientHandlerExtensions.CanUseClientCertificates)
                {
                    Debug.Assert(this.deviceEnrollment.State == DeviceEnrollmentState.Enrolled);
                    Debug.Assert(this.deviceEnrollment.Certificate != null);

                    var added = handler.TryAddClientCertificate(this.deviceEnrollment.Certificate);
                    Debug.Assert(added);
                }

                return handler;
            }
        }
    }
}
