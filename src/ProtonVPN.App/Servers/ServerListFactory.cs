﻿/*
 * Copyright (c) 2021 Proton Technologies AG
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

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using ProtonVPN.Common.Vpn;
using ProtonVPN.Config.Url;
using ProtonVPN.Core.Models;
using ProtonVPN.Core.Servers;
using ProtonVPN.Core.Servers.Specs;
using ProtonVPN.Core.Settings;
using ProtonVPN.Core.Vpn;
using ProtonVPN.Streaming;
using ProtonVPN.Translations;

namespace ProtonVPN.Servers
{
    internal class ServerListFactory : IVpnStateAware
    {
        private readonly ServerManager _serverManager;
        private readonly IUserStorage _userStorage;
        private readonly IStreamingServices _streamingServices;
        private readonly IActiveUrls _urls;
        private VpnState _vpnState = new(VpnStatus.Disconnected);

        public ServerListFactory(
            ServerManager serverManager,
            IUserStorage userStorage,
            IStreamingServices streamingServices,
            IActiveUrls urls)
        {
            _serverManager = serverManager;
            _userStorage = userStorage;
            _streamingServices = streamingServices;
            _urls = urls;
        }

        public ObservableCollection<IServerListItem> BuildServerList(string searchQuery = null)
        {
            switch (_userStorage.User().MaxTier)
            {
                case ServerTiers.Internal:
                case ServerTiers.Plus:
                    return GetPlusUserLocations(searchQuery);
                case ServerTiers.Basic:
                    return GetBasicUserLocations(searchQuery);
                case ServerTiers.Free:
                    return GetFreeUserLocations(searchQuery);
                default:
                    return new ObservableCollection<IServerListItem>();
            }
        }

        private ObservableCollection<IServerListItem> GetFreeUserLocations(string searchQuery)
        {
            var list = new ObservableCollection<IServerListItem>();
            IList<string> freeCountries = GetCountriesByTiers(ServerTiers.Free);
            IList<string> basicAndPlusCountries = GetCountriesByTiers(ServerTiers.Basic, ServerTiers.Plus)
                .Except(freeCountries).ToList();

            IList<IServerListItem> freeLocationViewModels =
                CreateServersByCountryViewModels(freeCountries, searchQuery).ToList();

            list = GetServerGroupViewModels(
                list,
                freeLocationViewModels,
                Translation.Format("Sidebar_Countries_FreeLocationCount", freeLocationViewModels.Count));

            IList<IServerListItem> basicAndPlusLocationsViewModels =
                CreateServersByCountryViewModels(basicAndPlusCountries, searchQuery).ToList();

            list = GetServerGroupViewModels(
                list,
                basicAndPlusLocationsViewModels,
                Translation.Format("Sidebar_Countries_BasicAndPlusLocationCount",
                    basicAndPlusLocationsViewModels.Count));

            return list;
        }

        private ObservableCollection<IServerListItem> GetPlusUserLocations(string searchQuery)
        {
            IList<string> countries = _serverManager.GetEntryCountriesBySpec(new StandardServer())
                .OrderBy(Countries.GetName)
                .ToList();
            IList<IServerListItem> plusLocationViewModels =
                CreateServersByCountryViewModels(countries, searchQuery).ToList();

            return GetServerGroupViewModels(
                new ObservableCollection<IServerListItem>(),
                plusLocationViewModels,
                Translation.Format("Sidebar_Countries_AllLocationCount", plusLocationViewModels.Count));
        }

        private ObservableCollection<IServerListItem> GetBasicUserLocations(string searchQuery)
        {
            var list = new ObservableCollection<IServerListItem>();
            IList<string> basicCountries = GetCountriesByTiers(ServerTiers.Basic);
            IList<string> plusCountries = GetCountriesByTiers(ServerTiers.Plus).Except(basicCountries).ToList();

            IList<IServerListItem> basicLocationViewModels =
                CreateServersByCountryViewModels(basicCountries, searchQuery).ToList();

            list = GetServerGroupViewModels(list, basicLocationViewModels,
                Translation.Format("Sidebar_Countries_BasicLocationCount", basicLocationViewModels.Count));

            if (plusCountries.Count > 0)
            {
                IList<IServerListItem> plusLocationViewModels =
                    CreateServersByCountryViewModels(plusCountries, searchQuery).ToList();

                list = GetServerGroupViewModels(list, plusLocationViewModels,
                    Translation.Format("Sidebar_Countries_PlusLocationCount", plusLocationViewModels.Count));
            }

            return list;
        }

        private ObservableCollection<IServerListItem> GetServerGroupViewModels(
            ObservableCollection<IServerListItem> list, IList<IServerListItem> viewModels, string groupName)
        {
            if (viewModels.Count > 0)
            {
                list.Add(CreateCountryListSeparator(groupName));

                foreach (IServerListItem c in viewModels)
                {
                    list.Add(c);
                }
            }

            return list;
        }

        public ObservableCollection<IServerListItem> BuildSecureCoreList(string searchQuery = "")
        {
            ObservableCollection<IServerListItem> serverListItems = new ObservableCollection<IServerListItem>();
            IOrderedEnumerable<string> countries = _serverManager.GetSecureCoreCountries().OrderBy(Countries.GetName);
            User user = _userStorage.User();

            foreach (string countryCode in countries)
            {
                if (string.IsNullOrEmpty(searchQuery) || Countries.MatchesSearch(countryCode, searchQuery))
                {
                    ServersByExitNodeViewModel row =
                        new ServersByExitNodeViewModel(countryCode, user.MaxTier, _serverManager);
                    row.LoadServers();
                    serverListItems.Add(row);
                }
            }

            return serverListItems;
        }

        public ObservableCollection<IServerListItem> BuildPortForwardingList(string searchQuery = null)
        {
            IList<string> p2PList = _serverManager.GetEntryCountriesBySpec(new P2PServer()).OrderBy(Countries.GetName)
                .ToList();
            IList<string> nonP2PList =
                _serverManager.GetCountries().Except(p2PList).OrderBy(Countries.GetName).ToList();

            List<IServerListItem> serverListItems = new List<IServerListItem>();
            List<IServerListItem> p2pViewModels = CreateServersByCountryViewModels(p2PList, searchQuery).ToList();
            if (p2pViewModels.Count > 0)
            {
                serverListItems.Add(CreateCountryListSeparator(Translation.Get("ServerType_val_P2P")));
                serverListItems.AddRange(p2pViewModels);
            }

            List<IServerListItem> otherViewModels = CreateServersByCountryViewModels(nonP2PList, searchQuery).ToList();
            if (otherViewModels.Count > 0)
            {
                serverListItems.Add(CreateCountryListSeparator(Translation.Get("Sidebar_Separator_Others")));
                serverListItems.AddRange(otherViewModels);
            }

            return new ObservableCollection<IServerListItem>(serverListItems);
        }

        private IEnumerable<IServerListItem> CreateServersByCountryViewModels(IList<string> countries,
            string searchQuery)
        {
            foreach (string countryCode in countries)
            {
                ServersByCountryViewModel countryViewModel = new(countryCode, _userStorage.User().MaxTier,
                    _serverManager, _vpnState, _streamingServices);
                if (string.IsNullOrEmpty(searchQuery) || Countries.MatchesSearch(countryCode, searchQuery))
                {
                    countryViewModel.LoadServers();
                    yield return countryViewModel;
                }
                else if (!string.IsNullOrEmpty(searchQuery))
                {
                    countryViewModel.LoadServers(searchQuery);
                    if (countryViewModel.Servers.Count > 0)
                    {
                        yield return countryViewModel;
                    }
                }
            }
        }

        private IServerListItem CreateCountryListSeparator(string name)
        {
            return new CountrySeparatorViewModel(_urls) {Name = name};
        }

        public Task OnVpnStateChanged(VpnStateChangedEventArgs e)
        {
            _vpnState = e.State;

            return Task.CompletedTask;
        }

        private List<string> GetCountriesByTiers(params sbyte[] tiers)
        {
            return _serverManager.GetCountriesByTier(tiers).OrderBy(Countries.GetName).ToList();
        }
    }
}