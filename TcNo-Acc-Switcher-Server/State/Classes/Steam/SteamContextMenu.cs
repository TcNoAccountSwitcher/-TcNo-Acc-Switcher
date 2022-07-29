﻿// TcNo Account Switcher - A Super fast account switcher
// Copyright (C) 2019-2022 TechNobo (Wesley Pyburn)
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Gameloop.Vdf;
using Gameloop.Vdf.JsonConverter;
using Microsoft.JSInterop;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;
using TcNo_Acc_Switcher_Globals;
using TcNo_Acc_Switcher_Server.Converters;
using TcNo_Acc_Switcher_Server.Pages.General.Classes;
using TcNo_Acc_Switcher_Server.Shared;
using TcNo_Acc_Switcher_Server.Shared.ContextMenu;
using TcNo_Acc_Switcher_Server.State.DataTypes;
using TcNo_Acc_Switcher_Server.State.Interfaces;

namespace TcNo_Acc_Switcher_Server.State.Classes.Steam;

public class SteamContextMenu
{
    private readonly IJSRuntime _jsRuntime;

    private readonly IAppState _appState;
    private readonly IGameStats _gameStats;
    private readonly ILang _lang;
    private readonly IModals _modals;
    private readonly ISharedFunctions _sharedFunctions;
    private readonly ISteamFuncs _steamFuncs;
    private readonly ISteamSettings _steamSettings;
    private readonly ISteamState _steamState;
    private readonly IToasts _toasts;

    public SteamContextMenu() {}
    public SteamContextMenu(IJSRuntime jsRuntime, IAppState appState, IGameStats gameStats, ILang lang, IModals modals, ISharedFunctions sharedFunctions,
        ISteamFuncs steamFuncs, ISteamSettings steamSettings, ISteamState steamState, IToasts toasts)
    {
        _jsRuntime = jsRuntime;
        _appState = appState;
        _gameStats = gameStats;
        _lang = lang;
        _modals = modals;
        _sharedFunctions = sharedFunctions;
        _steamFuncs = steamFuncs;
        _steamSettings = steamSettings;
        _steamState = steamState;
        _toasts = toasts;

        // Check registry for games list if Windows, whether installed, and their names
        // Else if it fails, or is not windows:
        //   Future cross-platform compatibility, or fallback:
        //   Download a list of all apps on Steam, and cross-check with Steam\\config\\libraryfolders.vdf
        if (!OperatingSystem.IsWindows() || !LoadGamesFromRegistry())
        {
            LoadInstalledGames();
            _steamState.AppIds = LoadAppNames();
        }

        BuildContextMenu();

        ShortcutItems = new MenuBuilder(_lang,
            new Tuple<string, object>[]
            {
                new ("Context_RunAdmin", ShortcutStartSteamAdmin),
                new ("Context_Hide", HideShortcutSteam),
            }).Result();

        PlatformItems = new MenuBuilder(_lang,
            new Tuple<string, object>("Context_RunAdmin", () => steamState.RunSteam(true, ""))
        ).Result();
    }

    [SupportedOSPlatform("windows")]
    private bool LoadGamesFromRegistry()
    {
        try
        {
            _steamState.InstalledGames = new List<string>(); // List of AppIds
            _steamState.AppIds = new Dictionary<string, string>(); // Dictionary of AppIDs and their name.

            // Open Key
            using var hkcu = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\Apps\", true);
            if (hkcu is null) return false;
            foreach (var appKeyString in hkcu.GetSubKeyNames())
            {
                // Foreach subkey (AppID) in Steam Apps
                try
                {
                    // Open the key, and look for components.
                    // These keys will have Name if they are NOT DLC.
                    // These also have an Installed value.
                    using var subkey = hkcu.OpenSubKey(appKeyString);
                    if (subkey is null) continue;

                    var name = subkey.GetValue("Name");
                    if (name is null) continue; // Has no name >> DLC.

                    var installed = subkey.GetValue("Installed");
                    if (installed is null || (int)installed != 1) continue; // Has no installed value (never installed), or is not installed.

                    // At this point: it is a game and it is installed. Add it to the lists.
                    var appId = subkey.ToString().Split("\\").Last();
                    _steamState.InstalledGames.Add(appId);
                    _steamState.AppIds.Add(appId, name.ToString());
                }
                catch
                {
                    continue;
                }
            }

            return true;
        }
        catch (Exception e)
        {
            Globals.WriteToLog("Failed to read Steam games from registry", e);
            return false;
        }
    }

    private const string ShortcutFolder = "LoginCache\\Steam\\Shortcuts\\";
    private void ShortcutStartSteamAdmin() =>
        _sharedFunctions.RunShortcut(_appState.Switcher.CurrentShortcut, ShortcutFolder, "Steam", true);
    private void HideShortcutSteam()
    {
        // Remove shortcut from folder, and list.
        _steamSettings.Shortcuts.Remove(_steamSettings.Shortcuts.First(e => e.Value == _appState.Switcher.CurrentShortcut).Key);
        var f = Path.Join(ShortcutFolder, _appState.Switcher.CurrentShortcut);
        if (File.Exists(f)) File.Move(f, f.Replace(".lnk", "_ignored.lnk").Replace(".url", "_ignored.url"));

        // Save.
        _steamSettings.Save();
    }

    public static readonly string SteamAppsListPath =
        Path.Join(Globals.UserDataFolder, "LoginCache\\Steam\\AppIdsFullListCache.json");

    public static readonly string SteamAppsUserCache =
        Path.Join(Globals.UserDataFolder, "LoginCache\\Steam\\AppIdsUser.json");

    public ObservableCollection<MenuItem> Menu = new();
    public ObservableCollection<MenuItem> ShortcutItems = new();
    public ObservableCollection<MenuItem> PlatformItems = new();

    public void BuildContextMenu()
    {
        Menu.Clear();

        /* Games submenu, or Game data item */
        MenuItem gameData = null;
        if (File.Exists(SteamAppsUserCache) && _steamState.AppIds.Count > 0)
        {
            var menuItems = new List<MenuItem>();
            foreach (var gameId in _steamState.InstalledGames)
            {
                menuItems.Add(new MenuItem
                {
                    Text = _steamState.AppIds.ContainsKey(gameId) ? _steamState.AppIds[gameId] : gameId,
                    Children = new List<MenuItem>
                    {
                        new()
                        {
                            Text = _lang["Context_Game_CopySettingsFrom"],
                            MenuAction = () => _steamFuncs.CopySettingsFrom(gameId)
                        },
                        new()
                        {
                            Text = _lang["Context_Game_RestoreSettingsTo"],
                            MenuAction = () => _steamFuncs.RestoreSettingsTo(gameId)
                        },
                        new()
                        {
                            Text = _lang["Context_Game_BackupData"],
                            MenuAction = () => _steamFuncs.BackupGameData(gameId)
                        }
                    }
                });
            }

            gameData = new MenuItem
            {
                Text = _lang["Context_GameDataSubmenu"],
                Children = menuItems
            };
        }

        // Prepare menu
        var menuBuilder = new MenuBuilder(_lang, new Tuple<string, object>[]
        {
            new("Context_SwapTo", new Action(async () => await _steamFuncs.SwapToAccount(_jsRuntime))),
            new("Context_LoginAsSubmenu", new Tuple<string, object>[]
                {
                    new("Invisible", new Action(async () => await _steamFuncs.SwapToAccount(_jsRuntime, 7))),
                    new("Offline", new Action(async () => await _steamFuncs.SwapToAccount(_jsRuntime, 0))),
                    new("Online", new Action(async () => await _steamFuncs.SwapToAccount(_jsRuntime, 1))),
                    new("Busy", new Action(async () => await _steamFuncs.SwapToAccount(_jsRuntime, 2))),
                    new("Away", new Action(async () => await _steamFuncs.SwapToAccount(_jsRuntime, 3))),
                    new("Snooze", new Action(async () => await _steamFuncs.SwapToAccount(_jsRuntime, 4))),
                    new("LookingToTrade", new Action(async () => await _steamFuncs.SwapToAccount(_jsRuntime, 5))),
                    new("LookingToPlay", new Action(async () => await _steamFuncs.SwapToAccount(_jsRuntime, 6))),
                }
            ),
            new("Context_CopySubmenu", new Tuple<string, object>[]
            {
                new("Context_CopyProfileSubmenu", new Tuple<string, object>[]
                {
                    new("Context_CommunityUrl",
                        new Action(() =>
                            StaticFuncs.CopyText(
                                $"https://steamcommunity.com/profiles/{_appState.Switcher.SelectedAccountId}"))),
                    new("Context_CommunityUsername",
                        new Action(() => StaticFuncs.CopyText(_appState.Switcher.SelectedAccount.DisplayName))),
                    new("Context_LoginUsername",
                        new Action(() => StaticFuncs.CopyText(_appState.Switcher.SelectedAccount.LoginUsername))),
                }),
                new("Context_CopySteamIdSubmenu", new Tuple<string, object>[]
                {
                    new("Context_Steam_Id",
                        new Action(() =>
                            StaticFuncs.CopyText(new SteamIdConvert(_appState.Switcher.SelectedAccountId).Id))),
                    new("Context_Steam_Id3",
                        new Action(() =>
                            StaticFuncs.CopyText(new SteamIdConvert(_appState.Switcher.SelectedAccountId).Id3))),
                    new("Context_Steam_Id32",
                        new Action(() =>
                            StaticFuncs.CopyText(new SteamIdConvert(_appState.Switcher.SelectedAccountId).Id32))),
                    new("Context_Steam_Id64",
                        new Action(() =>
                            StaticFuncs.CopyText(new SteamIdConvert(_appState.Switcher.SelectedAccountId).Id64))),
                }),
                new("Context_CopyOtherSubmenu", new Tuple<string, object>[]
                {
                    new("SteamRep",
                        new Action(() =>
                            StaticFuncs.CopyText($"https://steamrep.com/search?q={_appState.Switcher.SelectedAccountId}"))),
                    new("SteamID.uk",
                        new Action(() =>
                            StaticFuncs.CopyText($"https://steamid.uk/profile/{_appState.Switcher.SelectedAccountId}"))),
                    new("SteamID.io",
                        new Action(() =>
                            StaticFuncs.CopyText($"https://steamid.io/lookup/{_appState.Switcher.SelectedAccountId}"))),
                    new("SteamIDFinder.com",
                        new Action(() =>
                            StaticFuncs.CopyText(
                                $"https://steamidfinder.com/lookup/{_appState.Switcher.SelectedAccountId}"))),
                }),
            }),
            new("Context_CreateShortcut", new Tuple<string, object>[]
            {
                new("OnlineDefault", new Action(() => CreateShortcut())),
                new("Invisible", new Action(() => CreateShortcut(":7"))),
                new("Offline", new Action(() => CreateShortcut(":0"))),
                new("Busy", new Action(() => CreateShortcut(":2"))),
                new("Away", new Action(() => CreateShortcut(":3"))),
                new("Snooze", new Action(() => CreateShortcut(":4"))),
                new("LookingToTrade", new Action(() => CreateShortcut(":5"))),
                new("LookingToPlay", new Action(() => CreateShortcut(":6"))),
            }),
            new("Forget", new Action(() => _steamFuncs.ForgetAccount())),
            new("Notes", new Action(() => _modals.ShowModal("notes"))),
            new("Context_ManageSubmenu", new[]
            {
                gameData is not null
                    ? new Tuple<string, object>("Context_GameDataSubmenu", gameData)
                    : null,
                _gameStats.PlatformHasAnyGames("Steam")
                    ? new Tuple<string, object>("Context_ManageGameStats",
                        new Action(_modals.ShowGameStatsSelectorModal))
                    : null,
                new("Context_ChangeImage", new Action(_modals.ShowChangeAccImageModal)),
                new("Context_Steam_OpenUserdata", new Action(SteamOpenUserdata)),
                new("Context_ChangeName", new Action(_modals.ShowChangeUsernameModal)),
            })
        });

        Menu.AddRange(menuBuilder.Result());
    }

    /// <summary>
    /// Creates a shortcut to start the Account Switcher, and swap to the account related.
    /// </summary>
    /// <param name="args">(Optional) arguments for shortcut</param>
    public void CreateShortcut(string args = "")
    {
        if (!OperatingSystem.IsWindows()) return;
        Globals.DebugWriteLine(@"[JSInvoke:General\GeneralInvocableFuncs.CreateShortcut]");
        if (args.Length > 0 && args[0] != ':') args = $" {args}"; // Add a space before arguments if doesn't start with ':'
        var primaryPlatformId = "" + _appState.Switcher.CurrentSwitcher[0];
        var bgImg = Path.Join(Globals.WwwRoot, $"\\img\\platform\\{_appState.Switcher.CurrentSwitcherSafe}.svg");
        var currentPlatformImgPath = Path.Join(Globals.WwwRoot, "\\img\\platform\\Steam.svg");
        var currentPlatformImgPathOverride = Path.Join(Globals.WwwRoot, "\\img\\platform\\Steam.png");
        var ePersonaState = -1;
        if (args.Length == 2) _ = int.TryParse(args[1].ToString(), out ePersonaState);
        var platformName = $"Switch to {_appState.Switcher.SelectedAccount.DisplayName} {(args.Length > 0 ? $"({_steamFuncs.PersonaStateToString(ePersonaState)})" : "")} [{_appState.Switcher.CurrentSwitcher}]";

        if (File.Exists(currentPlatformImgPathOverride))
            bgImg = currentPlatformImgPathOverride;
        else if (File.Exists(currentPlatformImgPath))
            bgImg = currentPlatformImgPath;
        else if (File.Exists(Path.Join(Globals.WwwRoot, "\\img\\BasicDefault.png")))
            bgImg = Path.Join(Globals.WwwRoot, "\\img\\BasicDefault.png");


        var fgImg = Path.Join(Globals.WwwRoot, $"\\img\\profiles\\{_appState.Switcher.CurrentSwitcherSafe}\\{_appState.Switcher.SelectedAccountId}.jpg");
        if (!File.Exists(fgImg)) fgImg = Path.Join(Globals.WwwRoot, $"\\img\\profiles\\{_appState.Switcher.CurrentSwitcherSafe}\\{_appState.Switcher.SelectedAccountId}.png");
        if (!File.Exists(fgImg))
        {
            _toasts.ShowToastLang(ToastType.Error, "Toast_CantCreateShortcut", "Toast_CantFindImage");
            return;
        }

        var s = new Shortcut();
        _ = s.Shortcut_Platform(
            Shortcut.Desktop,
            platformName,
            $"+{primaryPlatformId}:{_appState.Switcher.SelectedAccountId}{args}",
            $"Switch to {_appState.Switcher.SelectedAccount.DisplayName} [{_appState.Switcher.CurrentSwitcher}] in TcNo Account Switcher",
            true);
        if (s.CreateCombinedIcon(bgImg, fgImg, $"{_appState.Switcher.SelectedAccountId}.ico"))
        {
            s.TryWrite();

            if (_appState.Stylesheet.StreamerModeTriggered)
                _toasts.ShowToastLang(ToastType.Success, "Success", "Toast_ShortcutCreated");
            else
                _toasts.ShowToastLang(ToastType.Success, "Toast_ShortcutCreated", new LangSub("ForName", new { name = _appState.Switcher.SelectedAccount.DisplayName }));
        }
        else
            _toasts.ShowToastLang(ToastType.Error, "Toast_FailedCreateIcon");
    }

    public void SteamOpenUserdata()
    {
        var steamId32 = new SteamIdConvert(_appState.Switcher.SelectedAccountId);
        var folder = Path.Join(_steamSettings.FolderPath, $"userdata\\{steamId32.Id32}");
        if (Directory.Exists(folder))
            _ = Process.Start("explorer.exe", folder);
        else
            _toasts.ShowToastLang(ToastType.Error, "Failed", "Toast_NoFindSteamUserdata");
    }
    public void LoadInstalledGames()
    {
        List<string> gameIds;
        try
        {
            var libraryVdf = VdfConvert.Deserialize(File.ReadAllText(_steamSettings.LibraryVdf));
            var library = new JObject { libraryVdf.ToJson() };
            gameIds = library["libraryfolders"]!
                .SelectMany(folder => ((JObject)folder.First?["apps"])?.Properties()
                    .Select(p => p.Name))
                .ToList();
        }
        catch (Exception e)
        {
            Globals.WriteToLog("ERROR: Could not fetch Steam game library.\nDetails: " + e);
            gameIds = new List<string>();
        }
        _steamState.InstalledGames = gameIds;
    }
    public Dictionary<string, string> LoadAppNames()
    {
        // Check if cached Steam AppId list is downloaded
        // If not, skip. Download is handled in a background task.
        if (!File.Exists(SteamAppsListPath))
        {
            // Download Steam AppId list if not already.
            Task.Run(DownloadSteamAppsData).ContinueWith(_ =>
            {
                var names = LoadAppNames();
                foreach (var kv in names)
                {
                    try
                    {
                        _steamState.AppIds.Add(kv.Key, kv.Value);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e.ToString());
                    }
                }
            });
            return new Dictionary<string, string>();
        }

        var cacheFilePath = Path.Join(Globals.UserDataFolder, "LoginCache\\Steam\\AppIdsUser.json");
        var appIds = new Dictionary<string, string>();
        try
        {
            // Check if all the IDs we need are in the cache, i.e. the user has not installed any new games.
            if (File.Exists(cacheFilePath))
            {
                var cachedAppIds = ParseSteamAppsText(File.ReadAllText(cacheFilePath));
                if (_steamState.InstalledGames.All(id => cachedAppIds.ContainsKey(id)))
                {
                    return cachedAppIds;
                }
            }

            // If the cache is missing or incomplete, fetch app Ids from Steam's API
            appIds =
                (from game in ParseSteamAppsText(FetchSteamAppsData())
                    where _steamState.InstalledGames.Contains(game.Key)
                    select game)
                .ToDictionary(game => game.Key, game => game.Value);

            // Downloading app list for the first time.
            if (appIds.Count == 0) return appIds;

            // Add any missing games as just the appid. These can include games/apps not on steam (developer Steam accounts), or otherwise removed games from Steam.
            if (appIds.Count != _steamState.InstalledGames.Count)
            {
                foreach (var g in (from game in _steamState.InstalledGames where !appIds.ContainsKey(game) select game))
                {
                    appIds.Add(g, g);
                }
            }


            // Write the IDs of currently installed games to the cache
            dynamic cacheObject = new System.Dynamic.ExpandoObject();
            cacheObject.applist = new System.Dynamic.ExpandoObject();
            cacheObject.applist.apps = (from app in appIds
                select new { appid = app.Key, name = app.Value }).ToArray();
            File.WriteAllText(cacheFilePath, JObject.FromObject(cacheObject).ToString(Newtonsoft.Json.Formatting.None));
        }
        catch (Exception e)
        {
            Globals.DebugWriteLine($@"Error Loading names for Steam game IDs: {e}");
        }
        return appIds;
    }
    public async Task DownloadSteamAppsData()
    {
        _toasts.ShowToastLang(ToastType.Info, "Toast_Steam_DownloadingAppIds");

        try
        {
            // Save to file
            var file = new FileInfo(SteamAppsListPath);
            if (file.Exists) file.Delete();
            if (Globals.ReadWebUrl("https://api.steampowered.com/ISteamApps/GetAppList/v2/", out var appList))
                await File.WriteAllTextAsync(file.FullName, appList);
            else
                throw new Exception("Failed to download Steam apps list.");
        }
        catch (Exception e)
        {
            Globals.DebugWriteLine($@"Error downloading Steam app list: {e}");
        }

        _toasts.ShowToastLang(ToastType.Info, "Toast_Steam_DownloadingAppIdsComplete");
    }

    /// <summary>
    /// Given a JSON string fetched from Valve's API, return a dictionary mapping game IDs to names.
    /// </summary>
    /// <param name="text">A JSON string matching Valve's API format</param>
    /// <returns></returns>
    private static Dictionary<string, string> ParseSteamAppsText(string text)
    {
        if (text == "") return new Dictionary<string, string>();

        var appIds = new Dictionary<string, string>();
        try
        {
            var json = JObject.Parse(text);
            foreach (var app in json["applist"]?["apps"]!)
            {
                if (appIds.ContainsKey(app["appid"]!.Value<string>()!)) continue;
                appIds.Add(app["appid"].Value<string>()!, app["name"]!.Value<string>());
            }
        }
        catch (Exception e)
        {
            Globals.DebugWriteLine($@"Error parsing Steam app list: {e}");
        }
        return appIds;
    }

    /// <summary>
    /// Fetches the names corresponding to each game ID from Valve's API.
    /// </summary>
    private static string FetchSteamAppsData()
    {
        // TODO: Copy the GitHub repo that downloads the latest apps, and shares as XML and CSV. Then remove those, and replace it with compressing with 7-zip. Download the latest 7-zip archive here, decompress then read. It takes literally ~1.5MB instead of ~8MB. HUGE saving for super slow internet.
        return File.Exists(SteamAppsListPath) ? File.ReadAllText(SteamAppsListPath) : "";
    }
}