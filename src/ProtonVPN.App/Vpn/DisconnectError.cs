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
using System.Dynamic;
using System.Threading;
using System.Threading.Tasks;
using ProtonVPN.Common.Networking;
using ProtonVPN.Common.Vpn;
using ProtonVPN.ConnectionInfo;
using ProtonVPN.Core.Auth;
using ProtonVPN.Core.Modals;
using ProtonVPN.Core.Models;
using ProtonVPN.Core.Service.Vpn;
using ProtonVPN.Core.Settings;
using ProtonVPN.Core.Vpn;
using ProtonVPN.Core.Window.Popups;
using ProtonVPN.Modals;
using ProtonVPN.Modals.SessionLimits;
using ProtonVPN.Notifications;
using ProtonVPN.Translations;
using ProtonVPN.Windows.Popups.Delinquency;

namespace ProtonVPN.Vpn
{
    public class DisconnectError : IVpnStateAware, ILogoutAware, ILoggedInAware
    {
        private readonly IModals _modals;
        private readonly IAppSettings _appSettings;
        private readonly IUserStorage _userStorage;
        private readonly MaximumDeviceLimitModalViewModel _maximumDeviceLimitModalViewModel;
        private readonly ConnectionErrorResolver _connectionErrorResolver;
        private readonly IPopupWindows _popupWindows;
        private readonly DelinquencyPopupViewModel _delinquencyPopupViewModel;
        private readonly IVpnManager _vpnManager;
        private readonly INotificationSender _notificationSender;
        private readonly IAuthCertificateManager _authCertificateManager;
        private readonly IVpnServiceManager _vpnServiceManager;

        private string _lastAuthCertificate = string.Empty;
        private bool _loggedIn;

        public DisconnectError(IModals modals, 
            IAppSettings appSettings,
            IUserStorage userStorage,
            MaximumDeviceLimitModalViewModel maximumDeviceLimitModalViewModel, 
            ConnectionErrorResolver connectionErrorResolver, 
            IPopupWindows popupWindows, 
            DelinquencyPopupViewModel delinquencyPopupViewModel, 
            IVpnManager vpnManager, 
            INotificationSender notificationSender,
            IAuthCertificateManager authCertificateManager,
            IVpnServiceManager vpnServiceManager)
        {
            _modals = modals;
            _appSettings = appSettings;
            _userStorage = userStorage;
            _maximumDeviceLimitModalViewModel = maximumDeviceLimitModalViewModel;
            _connectionErrorResolver = connectionErrorResolver;
            _popupWindows = popupWindows;
            _delinquencyPopupViewModel = delinquencyPopupViewModel;
            _vpnManager = vpnManager;
            _notificationSender = notificationSender;
            _authCertificateManager = authCertificateManager;
            _vpnServiceManager = vpnServiceManager;
        }

        public async Task OnVpnStateChanged(VpnStateChangedEventArgs e)
        {
            if (_appSettings.NetworkAdapterType == OpenVpnAdapter.Tun && e.Error == VpnError.TapAdapterInUseError)
            {
                _modals.Show<TunInUseModalViewModel>();
                return;
            }

            VpnStatus status = e.State.Status;

            switch (e.Error)
            {
                case VpnError.CertRevokedOrExpired:
                    await _authCertificateManager.ForceRequestNewKeyPairAndCertificateAsync();
                    await _vpnManager.ReconnectAsync(new VpnReconnectionSettings { IsToReconnectIfDisconnected = true });
                    return;
                case VpnError.CertificateExpired when e.State.Status == VpnStatus.ActionRequired:
                    _lastAuthCertificate = _appSettings.AuthenticationCertificatePem;
                    await _authCertificateManager.ForceRequestNewCertificateAsync();
                    if (FailedToUpdateAuthCert())
                    {
                        await _vpnManager.DisconnectAsync(VpnError.CertificateExpired);
                    }
                    else
                    {
                        await _vpnServiceManager.UpdateAuthCertificate(_appSettings.AuthenticationCertificatePem);
                    }
                    return;
            }

            if (ModalShouldBeShown(e))
            {
                Post(() => ShowModalAsync(e));
            }
            else
            {
                if (status == VpnStatus.Pinging ||
                    status == VpnStatus.Connecting ||
                    status == VpnStatus.Connected ||
                    (status == VpnStatus.Disconnecting ||
                     status == VpnStatus.Disconnected) &&
                    e.Error == VpnError.None)
                {
                    Post(CloseModalAsync);
                }
            }
        }

        private bool FailedToUpdateAuthCert()
        {
            return _lastAuthCertificate == _appSettings.AuthenticationCertificatePem;
        }

        private bool ModalShouldBeShown(VpnStateChangedEventArgs e)
        {
            return _loggedIn &&
                   e.Error != VpnError.NoneKeepEnabledKillSwitch &&
                   e.State.Status == VpnStatus.Disconnected &&
                   e.UnexpectedDisconnect;
        }

        private async Task ShowModalAsync(VpnStateChangedEventArgs e)
        {
            VpnError error = e.Error;
            if (error == VpnError.AuthorizationError)
            {
                error = await _connectionErrorResolver.ResolveError();
            }

            switch (error)
            {
                case VpnError.UserTierTooLowError:
                    await ForceReconnectAsync();
                    break;
                case VpnError.Unpaid:
                    await ShowDelinquencyPopupViewModelAsync();
                    break;
                case VpnError.SessionLimitReached:
                    ShowMaximumDeviceLimitModalViewModel();
                    break;
                default:
                    ShowDisconnectErrorModalViewModel(error, e.NetworkBlocked);
                    break;
            }
        }

        private async Task ForceReconnectAsync()
        {
            VpnReconnectionSettings reconnectionSettings = new()
            {
                IsToReconnectIfDisconnected = true
            };
            await _vpnManager.ReconnectAsync(reconnectionSettings);
        }

        private async Task ShowDelinquencyPopupViewModelAsync()
        {
            _delinquencyPopupViewModel.SetNoReconnectionData();
            _popupWindows.Show<DelinquencyPopupViewModel>();
            await ForceReconnectAsync();
        }

        private void ShowMaximumDeviceLimitModalViewModel()
        {
            bool hasMaxTierPlan = HasMaxTierPlan();

            string notificationDescription = hasMaxTierPlan
                ? Translation.Get("Notifications_MaximumDeviceLimit_Disconnect_Description")
                : Translation.Get("Notifications_MaximumDeviceLimit_Upgrade_Description");
            _notificationSender.Send(Translation.Get("Notifications_MaximumDeviceLimit_Title"), 
                notificationDescription);
            
            _maximumDeviceLimitModalViewModel.SetPlan(hasMaxTierPlan);
            _modals.Show<MaximumDeviceLimitModalViewModel>();
        }

        private bool HasMaxTierPlan()
        {
            User user = _userStorage.User();
            switch (user.OriginalVpnPlan)
            {
                case "vpnplus":
                case "visionary":
                    return true;
                default:
                    return false;
            }
        }

        private void ShowDisconnectErrorModalViewModel(VpnError error, bool networkBlocked)
        {
            dynamic options = new ExpandoObject();
            options.Error = error;
            options.NetworkBlocked = networkBlocked;

            _modals.Show<DisconnectErrorModalViewModel>(options);
        }

        private async Task CloseModalAsync()
        {
            _modals.Close<MaximumDeviceLimitModalViewModel>();
            _modals.Close<DisconnectErrorModalViewModel>();
        }

        private void Post(Func<Task> action)
        {
            SynchronizationContext.Current.Post(async _ => await action(), null);
        }

        public void OnUserLoggedOut()
        {
            _loggedIn = false;
        }

        public void OnUserLoggedIn()
        {
            _loggedIn = true;
        }
    }
}