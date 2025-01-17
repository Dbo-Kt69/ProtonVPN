﻿/*
 * Copyright (c) 2020 Proton Technologies AG
 *
 * This file is part of ProtonVPN.
 *
 * ProtonVPN is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * ProtonVPN is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with ProtonVPN.  If not, see <https://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using ProtonVPN.BugReporting.Attachments;
using ProtonVPN.BugReporting.Diagnostic;
using ProtonVPN.Common.Abstract;
using ProtonVPN.Common.Extensions;
using ProtonVPN.Core.Api;

namespace ProtonVPN.BugReporting
{
    public class BugReport : IBugReport
    {
        private readonly IApiClient _apiClient;
        private readonly Attachments.Attachments _attachments;
        private readonly NetworkLogWriter _networkLogWriter;

        public BugReport(IApiClient apiClient, Attachments.Attachments attachments, NetworkLogWriter networkLogWriter)
        {
            _networkLogWriter = networkLogWriter;
            _apiClient = apiClient;
            _attachments = attachments;
        }

        public async Task<Result> SendAsync(KeyValuePair<string, string>[] fields)
        {
            return await SendInternalAsync(fields, new List<File>());
        }

        public async Task<Result> SendWithLogsAsync(KeyValuePair<string, string>[] fields)
        {
            await _networkLogWriter.WriteAsync();
            return await SendInternalAsync(fields, new AttachmentsToApiFiles(_attachments.Get()));
        }

        private async Task<Result> SendInternalAsync(KeyValuePair<string, string>[] fields, IEnumerable<File> files)
        {
            try
            {
                return await _apiClient.ReportBugAsync(fields, files);
            }
            catch (Exception e) when (e is HttpRequestException || e.IsFileAccessException())
            {
                return Result.Fail(e.Message);
            }
        }
    }
}