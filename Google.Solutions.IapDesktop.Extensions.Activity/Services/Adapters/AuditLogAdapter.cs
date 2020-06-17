﻿//
// Copyright 2019 Google LLC
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
using Google.Apis.Logging.v2;
using Google.Apis.Logging.v2.Data;
using Google.Apis.Services;
using Google.Apis.Util;
using Google.Solutions.Common.ApiExtensions.Request;
using Google.Solutions.Common.Diagnostics;
using Google.Solutions.IapDesktop.Application;
using Google.Solutions.IapDesktop.Application.ObjectModel;
using Google.Solutions.IapDesktop.Application.Services.Adapters;
using Google.Solutions.IapDesktop.Extensions.Activity.Events;
using Google.Solutions.IapDesktop.Extensions.Activity.History;
using Google.Solutions.IapDesktop.Extensions.Activity.Logs;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Google.Solutions.IapDesktop.Extensions.Activity.Services.Adapters
{
    public interface IAuditLogAdapter
    {
        Task ListInstanceEventsAsync(
            IEnumerable<string> projectIds,
            IEnumerable<string> zones,
            IEnumerable<ulong> instanceIds,
            DateTime startTime,
            IEventProcessor processor,
            CancellationToken cancellationToken);
    }

    [Service(typeof(IAuditLogAdapter), ServiceLifetime.Transient)]
    public class AuditLogAdapter : IAuditLogAdapter
    {
        private const int MaxPageSize = 1000;
        private const int MaxRetries = 10;
        private static readonly TimeSpan initialBackOff = TimeSpan.FromMilliseconds(100);

        private readonly LoggingService service;

        public AuditLogAdapter(ICredential credential)
        {
            this.service = new LoggingService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = Globals.UserAgent.ToApplicationName()
            });
        }

        public AuditLogAdapter(IServiceProvider serviceProvider)
            : this(serviceProvider.GetService<IAuthorizationAdapter>().Authorization.Credential)
        {
        }

        internal async Task ListEventsAsync(
            ListLogEntriesRequest request,
            Action<EventBase> callback,
            ExponentialBackOff backOff,
            CancellationToken cancellationToken)
        {
            using (TraceSources.IapDesktop.TraceMethod().WithParameters(request.Filter))
            {
                try
                {
                    string nextPageToken = null;
                    do
                    {
                        request.PageToken = nextPageToken;

                        using (var stream = await this.service.Entries
                            .List(request)
                            .ExecuteAsStreamWithRetryAsync(backOff, cancellationToken)
                            .ConfigureAwait(false))
                        using (var reader = new JsonTextReader(new StreamReader(stream)))
                        {
                            nextPageToken = ListLogEntriesParser.Read(reader, callback);
                        }
                    }
                    while (nextPageToken != null);
                }
                catch (GoogleApiException e) when (e.Error != null && e.Error.Code == 403)
                {
                    throw new ResourceAccessDeniedException(
                        $"Access to audit logs has been denied", e);
                }
            }
        }

        internal static string CreateFilterString(
            IEnumerable<string> zones,
            IEnumerable<ulong> instanceIds,
            IEnumerable<string> methods,
            IEnumerable<string> severities,
            DateTime startTime)
        {
            Debug.Assert(startTime.Kind == DateTimeKind.Utc);

            var criteria = new LinkedList<string>();

            if (zones != null && zones.Any())
            {
                criteria.AddLast($"resource.labels.zone=(\"{string.Join("\" OR \"", zones)}\")");
            }

            if (instanceIds != null && instanceIds.Any())
            {
                criteria.AddLast($"resource.labels.instance_id=(\"{string.Join("\" OR \"", instanceIds)}\")");
            }

            if (methods != null && methods.Any())
            {
                criteria.AddLast($"protoPayload.methodName=(\"{string.Join("\" OR \"", methods)}\")");
            }

            if (severities != null && severities.Any())
            {
                criteria.AddLast($"severity=(\"{string.Join("\" OR \"", severities)}\")");
            }

            criteria.AddLast($"resource.type=\"gce_instance\"");
            criteria.AddLast($"timestamp > \"{startTime.ToString("o")}\"");

            return string.Join(" AND ", criteria);
        }

        public async Task ListInstanceEventsAsync(
            IEnumerable<string> projectIds,
            IEnumerable<string> zones,
            IEnumerable<ulong> instanceIds,
            DateTime startTime,
            IEventProcessor processor,
            CancellationToken cancellationToken)
        {
            Utilities.ThrowIfNull(projectIds, nameof(projectIds));

            using (TraceSources.IapDesktop.TraceMethod().WithParameters(
                string.Join(", ", projectIds), 
                startTime))
            {
                var request = new ListLogEntriesRequest()
                {
                    ResourceNames = projectIds.Select(p => "projects/" + p).ToList(),
                    Filter = CreateFilterString(
                        zones,
                        instanceIds,
                        processor.SupportedMethods,
                        processor.SupportedSeverities,
                        startTime),
                    PageSize = MaxPageSize,
                    OrderBy = "timestamp desc"
                };

                await ListEventsAsync(
                    request,
                    processor.Process,
                    new ExponentialBackOff(initialBackOff, MaxRetries),
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
