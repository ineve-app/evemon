﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

using EVEMon.Common.Attributes;
using EVEMon.Common.CustomEventArgs;
using EVEMon.Common.Data;
using EVEMon.Common.Net;
using EVEMon.Common.Notifications;
using EVEMon.Common.Serialization.BattleClinic;
using EVEMon.Common.Threading;

namespace EVEMon.Common
{
    /// <summary>
    /// Provides a controller layer for the application. This class manages API querying, objects lifecycle, etc. 
    /// See it as the entry point of the library and its collections as databases with stored procedures (the public ones).
    /// </summary>
    [EnforceUIThreadAffinity]
    public static class EveMonClient
    {
        private static StreamWriter s_traceStream;
        private static TextWriterTraceListener s_traceListener;
        private static readonly DateTime s_startTime = DateTime.UtcNow;
        private static GlobalDatafileCollection s_datafiles;

        private static readonly Object s_pathsInitializationLock = new Object();
        private static readonly Object s_initializationLock = new Object();
        private static bool s_initialized;
        private static bool s_running;
        private static string s_traceFile;


        #region Initialization and threading

        /// <summary>
        /// Initializes paths, static objects, check and load datafiles, etc.
        /// </summary>
        /// <remarks>May be called more than once without causing redundant operations to occur.</remarks>
        public static void Initialize()
        {
            lock (s_initializationLock)
            {
                if (s_initialized)
                    return;

                s_initialized = true;

                Trace("EveMonClient.Initialize - begin");

                // Members instantiations
                HttpWebService = new HttpWebService();
                APIProviders = new GlobalAPIProviderCollection();
                MonitoredCharacters = new GlobalMonitoredCharacterCollection();
                CharacterIdentities = new GlobalCharacterIdentityCollection();
                Notifications = new GlobalNotificationCollection();
                Characters = new GlobalCharacterCollection();
                Datafiles = new GlobalDatafileCollection();
                Accounts = new GlobalAccountCollection();
                EVEServer = new EveServer();

                // Load static datas (min order to follow : skills before anything else, items before certs)
                Trace("Load Datafiles - begin");
                StaticProperties.Load();
                StaticSkills.Load();
                StaticItems.Load();
                StaticCertificates.Load();
                StaticBlueprints.Load();
                Trace("Load Datafiles - done");

                // Network monitoring (connection availability changes)
                NetworkMonitor.Initialize();

                Trace("EveMonClient.Initialize - done");
            }
        }

        /// <summary>
        /// Starts the event processing on a multi-threaded model, with the UI actor being the main actor.
        /// </summary>
        /// <param name="mainForm">The main form of the application</param>
        /// <remarks>May be called more than once without causing redundant operations to occur.</remarks>
        public static void Run(Form mainForm)
        {
            Trace("EveMonClient.Run");

            s_running = true;
            Dispatcher.Run(new UIActor(mainForm));
            UpdateOnOneSecondTick();
        }

        /// <summary>
        /// Shutdowns the timer
        /// </summary>
        public static void Shutdown()
        {
            Closed = true;
            s_running = false;
            Dispatcher.Shutdown();
        }

        /// <summary>
        /// Gets true whether the client has been shut down.
        /// </summary>
        public static bool Closed { get; private set; }

        /// <summary>
        /// Gets or sets a value indicating whether this instance is debug build.
        /// </summary>
        /// <value>
        /// 	<c>true</c> if this instance is debug build; otherwise, <c>false</c>.
        /// </value>
        public static bool IsDebugBuild { get; private set; }

        #endregion


        #region File paths

        /// <summary>
        /// Gets or sets the EVE Online installation's default portrait cache folder.
        /// </summary>
        public static string[] DefaultEvePortraitCacheFolders { get; private set; }

        /// <summary>
        /// Gets or sets the portrait cache folder defined by the user.
        /// </summary>
        public static string[] EvePortraitCacheFolders { get; private set; }

        /// <summary>
        /// Gets or sets the EVE Online application data folder.
        /// </summary>
        public static string EVEApplicationDataDir { get; private set; }

        /// <summary>
        /// Returns the current data storage directory.
        /// </summary>
        public static string EVEMonDataDir { get; private set; }

        /// <summary>
        /// Returns the current cache directory.
        /// </summary>
        public static string EVEMonCacheDir { get; private set; }

        /// <summary>
        /// Returns the current xml cache directory.
        /// </summary>
        public static string EVEMonXmlCacheDir { get; private set; }

        /// <summary>
        /// Returns the current image cache directory (not portraits).
        /// </summary>
        public static string EVEMonImageCacheDir { get; private set; }

        /// <summary>
        /// Returns the current portraits cache directory.
        /// </summary>
        /// <remarks>
        /// We're talking about the cache in %APPDATA%\cache\portraits
        /// This is different from the ImageService's hit cache (%APPDATA%\cache\image)
        /// or the game's portrait cache (in EVE Online folder)
        ///</remarks>
        public static string EVEMonPortraitCacheDir { get; private set; }

        /// <summary>
        /// Gets the name of the current settings file.
        /// </summary>
        public static string SettingsFileName { get; private set; }

        /// <summary>
        /// Gets the fully qualified path to the current settings file.
        /// </summary>
        public static string SettingsFileNameFullPath
        {
            get { return Path.Combine(EVEMonDataDir, SettingsFileName); }
        }

        /// <summary>
        /// Gets the fully qualified path to the trace file.
        /// </summary>
        public static string TraceFileNameFullPath
        {
            get { return Path.Combine(EVEMonDataDir, s_traceFile); }
        }

        /// <summary>
        /// Creates the file system paths (settings file name, appdata directory, etc).
        /// </summary>
        public static void InitializeFileSystemPaths()
        {
            lock (s_pathsInitializationLock)
            {
                // Ensure it is made once only
                if (!String.IsNullOrEmpty(SettingsFileName))
                    return;

                if (IsDebugBuild)
                {
                    SettingsFileName = "settings-debug.xml";
                    s_traceFile = "trace-debug.txt";
                }
                else
                {
                    SettingsFileName = "settings.xml";
                    s_traceFile = "trace.txt";
                }

                while (true)
                {
                    try
                    {
                        InitializeEVEMonPaths();
                        InitializeDefaultEvePortraitCachePath();
                        return;
                    }
                    catch (UnauthorizedAccessException exc)
                    {
                        string msg = String.Format("An error occurred while EVEMon was looking for its data directory. " +
                                                   "You may have insufficient rights or a synchronization may be taking place.{0}{0}The message was :{0}{1}",
                                                   Environment.NewLine, exc.Message);

                        DialogResult result = MessageBox.Show(msg, "EVEMon Error", MessageBoxButtons.RetryCancel,
                                                              MessageBoxIcon.Error);

                        if (result == DialogResult.Cancel)
                        {
                            Application.Exit();
                            return;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Initializes all needed EVEMon paths.
        /// </summary>
        private static void InitializeEVEMonPaths()
        {
            // If settings.xml exists in the app's directory, we use this one
            EVEMonDataDir = Directory.GetCurrentDirectory();
            string settingsFile = Path.Combine(EVEMonDataDir, SettingsFileName);

            // Else, we use %APPDATA%\EVEMon
            if (!File.Exists(settingsFile))
                EVEMonDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EVEMon");

            // Create the directory if it does not exist already
            if (!Directory.Exists(EVEMonDataDir))
                Directory.CreateDirectory(EVEMonDataDir);

            // Create the cache subfolder
            EVEMonCacheDir = Path.Combine(EVEMonDataDir, "cache");
            if (!Directory.Exists(EVEMonCacheDir))
                Directory.CreateDirectory(EVEMonCacheDir);

            // Create the xml cache subfolder
            EVEMonXmlCacheDir = Path.Combine(EVEMonCacheDir, "xml");
            if (!Directory.Exists(EVEMonXmlCacheDir))
                Directory.CreateDirectory(EVEMonXmlCacheDir);

            // Create the images cache subfolder (not portraits)
            EVEMonImageCacheDir = Path.Combine(EVEMonCacheDir, "images");
            if (!Directory.Exists(EVEMonImageCacheDir))
                Directory.CreateDirectory(EVEMonImageCacheDir);

            // Create the portraits cache subfolder
            EVEMonPortraitCacheDir = Path.Combine(EVEMonCacheDir, "portraits");
            if (!Directory.Exists(EVEMonPortraitCacheDir))
                Directory.CreateDirectory(EVEMonPortraitCacheDir);
        }

        /// <summary>
        /// Retrieves the portrait cache folder, from the game installation.
        /// </summary>
        private static void InitializeDefaultEvePortraitCachePath()
        {
            DefaultEvePortraitCacheFolders = new string[] {};
            string localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            EVEApplicationDataDir = String.Format(CultureConstants.DefaultCulture, "{1}{0}CCP{0}EVE",
                                                  Path.DirectorySeparatorChar, localApplicationData);

            // Check folder exists
            if (!Directory.Exists(EVEApplicationDataDir))
                return;

            // Create a pattern that matches anything "*_tranquility"
            // Enumerate files in the EVE cache directory
            DirectoryInfo di = new DirectoryInfo(EVEApplicationDataDir);
            DirectoryInfo[] filesInEveCache = di.GetDirectories("*_tranquility");

            if (filesInEveCache.Length == 0)
                return;

            EvePortraitCacheFolders = filesInEveCache.Select(
                eveDataPath => eveDataPath.Name).Select(
                    portraitCache => String.Format(CultureConstants.DefaultCulture, "{2}{0}{1}{0}cache{0}Pictures{0}Characters",
                                                   Path.DirectorySeparatorChar, portraitCache, EVEApplicationDataDir)).ToArray();

            DefaultEvePortraitCacheFolders = EvePortraitCacheFolders;
        }

        /// <summary>
        /// Set the EVE Online installation's portrait cache folder.
        /// </summary>
        /// <param name="path">location of the folder</param>
        internal static void SetEvePortraitCacheFolder(string[] path)
        {
            EvePortraitCacheFolders = path;
        }

        /// <summary>
        /// Ensures the cache directories are initialized.
        /// </summary>
        internal static void EnsureCacheDirInit()
        {
            InitializeEVEMonPaths();
        }

        #endregion


        #region Services

        /// <summary>
        /// Gets an enumeration over the datafiles checksums.
        /// </summary>
        public static GlobalDatafileCollection Datafiles
        {
            get
            {
                s_datafiles.Refresh();
                return s_datafiles;
            }
            private set { s_datafiles = value; }
        }

        /// <summary>
        /// Gets the http web service we use to query web services.
        /// </summary>
        public static HttpWebService HttpWebService { get; private set; }

        /// <summary>
        /// Gets the API providers collection.
        /// </summary>
        public static GlobalAPIProviderCollection APIProviders { get; private set; }

        /// <summary>
        /// Gets the EVE server's informations.
        /// </summary>
        public static EveServer EVEServer { get; private set; }

        /// <summary>
        /// Apply some settings changes.
        /// </summary>
        private static void UpdateSettings()
        {
            HttpWebService.State.Proxy = Settings.Proxy;
        }

        #endregion


        #region Cache Clearing

        public static void ClearCache()
        {
            try
            {
                List<FileInfo> cachedFiles = new List<FileInfo>();
                cachedFiles.AddRange(new DirectoryInfo(EVEMonImageCacheDir).GetFiles());
                cachedFiles.AddRange(new DirectoryInfo(EVEMonXmlCacheDir).GetFiles());
                cachedFiles.AddRange(new DirectoryInfo(EVEMonPortraitCacheDir).GetFiles());

                cachedFiles.ForEach(x => x.Delete());
            }
            finally
            {
                InitializeEVEMonPaths();
            }
        }

        #endregion


        #region Accounts management

        /// <summary>
        /// Gets the collection of all known accounts.
        /// </summary>
        public static GlobalAccountCollection Accounts { get; private set; }

        /// <summary>
        /// Gets the collection of all characters.
        /// </summary>
        public static GlobalCharacterCollection Characters { get; private set; }

        /// <summary>
        /// Gets the collection of all known character identities. For monitored character, see <see cref="MonitoredCharacters"/>.
        /// </summary>
        public static GlobalCharacterIdentityCollection CharacterIdentities { get; private set; }

        /// <summary>
        /// Gets the collection of all monitored characters.
        /// </summary>
        public static GlobalMonitoredCharacterCollection MonitoredCharacters { get; private set; }

        /// <summary>
        /// Gets the collection of notifications.
        /// </summary>
        public static GlobalNotificationCollection Notifications { get; private set; }

        /// <summary>
        /// Everytime the API timer is clicked, we fire this to check whether we need to update the queries.
        /// </summary>
        internal static void UpdateOnOneSecondTick()
        {
            if (!s_running)
                return;

            // Updates EVE server status
            EVEServer.UpdateOnOneSecondTick();

            // Updates the accounts
            foreach (Account account in Accounts)
            {
                account.UpdateOnOneSecondTick();
            }

            // Updates the characters
            foreach (CCPCharacter ccpCharacter in Characters.OfType<CCPCharacter>())
            {
                ccpCharacter.UpdateOnOneSecondTick();
            }

            // Fires the event for subscribers
            if (TimerTick != null)
                TimerTick(null, EventArgs.Empty);

            // Check for settings save
            Settings.UpdateOnOneSecondTick();
        }

        #endregion


        #region Events firing

        /// <summary>
        /// Occurs every second.
        /// </summary>
        public static event EventHandler TimerTick;

        /// <summary>
        /// Occurs when the scheduler entries changed.
        /// </summary>
        public static event EventHandler SchedulerChanged;

        /// <summary>
        /// Occurs when the settings changed.
        /// </summary>
        public static event EventHandler SettingsChanged;

        /// <summary>
        /// Occurs when the collection of accounts changed.
        /// </summary>
        public static event EventHandler AccountCollectionChanged;

        /// <summary>
        /// Occurs when the collection of characters changed.
        /// </summary>
        public static event EventHandler CharacterCollectionChanged;

        /// <summary>
        /// Occurs when the list of characters in an account has been updated.
        /// </summary>
        public static event EventHandler CharacterListUpdated;

        /// <summary>
        /// Occurs when the collection of monitored characters changed.
        /// </summary>
        public static event EventHandler MonitoredCharacterCollectionChanged;

        /// <summary>
        /// Occurs when a character training check on an account has been updated.
        /// </summary>
        public static event EventHandler AccountCharactersSkillInTrainingUpdated;

        /// <summary>
        /// Occurs when an account status has been updated.
        /// </summary>
        public static event EventHandler AccountStatusUpdated;

        /// <summary>
        /// Occurs when the conquerable station list has been updated.
        /// </summary>
        public static event EventHandler ConquerableStationListUpdated;

        /// <summary>
        /// Occurs when one or many queued skills have been completed.
        /// </summary>
        public static event EventHandler<QueuedSkillsEventArgs> QueuedSkillsCompleted;

        /// <summary>
        /// Occurs when one of the character's collection of plans changed.
        /// </summary>
        public static event EventHandler<CharacterChangedEventArgs> CharacterPlanCollectionChanged;

        /// <summary>
        /// Occurs when a character sheet has been updated.
        /// </summary>
        public static event EventHandler<CharacterChangedEventArgs> CharacterUpdated;

        /// <summary>
        /// Occurs when a character info has been updated.
        /// </summary>
        public static event EventHandler<CharacterChangedEventArgs> CharacterInfoUpdated;

        /// <summary>
        /// Occurs when a character skill queue has been updated.
        /// </summary>
        public static event EventHandler<CharacterChangedEventArgs> CharacterSkillQueueUpdated;

        /// <summary>
        /// Occurs when a character standings have been updated.
        /// </summary>
        public static event EventHandler<CharacterChangedEventArgs> CharacterStandingsUpdated;

        /// <summary>
        /// Occurs when a character's potrait has been updated.
        /// </summary>
        public static event EventHandler<CharacterChangedEventArgs> CharacterPortraitUpdated;

        /// <summary>
        /// Occurs when the market orders of a character have been updated.
        /// </summary>
        public static event EventHandler<CharacterChangedEventArgs> CharacterMarketOrdersUpdated;

        /// <summary>
        /// Occurs when the industry jobs of a character have been updated.
        /// </summary>
        public static event EventHandler<CharacterChangedEventArgs> CharacterIndustryJobsUpdated;

        /// <summary>
        /// Occurs when the industry jobs of a character have been completed.
        /// </summary>
        public static event EventHandler<IndustryJobsEventArgs> CharacterIndustryJobsCompleted;

        /// <summary>
        /// Occurs when the research points of a character have been updated.
        /// </summary>
        public static event EventHandler<CharacterChangedEventArgs> CharacterResearchPointsUpdated;

        /// <summary>
        /// Occurs when the mail messages of a character have been updated.
        /// </summary>
        public static event EventHandler<CharacterChangedEventArgs> CharacterEVEMailMessagesUpdated;

        /// <summary>
        /// Occurs when the body of a character EVE mail message has been downloaded.
        /// </summary>
        public static event EventHandler<CharacterChangedEventArgs> CharacterEVEMailBodyDownloaded;

        /// <summary>
        /// Occurs when the notifications of a character have been updated.
        /// </summary>
        public static event EventHandler<CharacterChangedEventArgs> CharacterEVENotificationsUpdated;

        /// <summary>
        /// Occurs when the text of a character EVE notification has been downloaded.
        /// </summary>
        public static event EventHandler<CharacterChangedEventArgs> CharacterEVENotificationTextDownloaded;

        /// <summary>
        /// Occurs when a plan's name changed.
        /// </summary>
        public static event EventHandler<PlanChangedEventArgs> PlanNameChanged;

        /// <summary>
        /// Occurs when a plan changed.
        /// </summary>
        public static event EventHandler<PlanChangedEventArgs> PlanChanged;

        /// <summary>
        /// Fired every time we ping the TQ server status (update pilots online count etc).
        /// </summary>
        public static event EventHandler<EveServerEventArgs> ServerStatusUpdated;

        /// <summary>
        /// Fired every time a notification (API errors, skill completed) is sent.
        /// </summary>
        public static event EventHandler<NotificationEventArgs> NotificationSent;

        /// <summary>
        /// Fired every time a notification (API errors, skill completed) is invalidated.
        /// </summary>
        public static event EventHandler<NotificationInvalidationEventArgs> NotificationInvalidated;

        /// <summary>
        /// Occurs when an application update is available.
        /// </summary>
        public static event EventHandler<UpdateAvailableEventArgs> UpdateAvailable;

        /// <summary>
        /// Occurs when a data files update is available.
        /// </summary>
        public static event EventHandler<DataUpdateAvailableEventArgs> DataUpdateAvailable;

        /// <summary>
        /// Occurs when  the BattleClinic API credentials is updated.
        /// </summary>
        public static event EventHandler<BCAPIEventArgs> BCAPICredentialsUpdated;

        /// <summary>
        /// Called when settings changed.
        /// </summary>
        internal static void OnSettingsChanged()
        {
            Trace("EveMonClient.OnSettingsChanged");
            Settings.Save();
            UpdateSettings();
            if (SettingsChanged != null)
                SettingsChanged(null, EventArgs.Empty);
        }

        /// <summary>
        /// Called when the scheduler changed.
        /// </summary>
        internal static void OnSchedulerChanged()
        {
            Trace("EveMonClient.OnSchedulerChanged");
            Settings.Save();
            if (SchedulerChanged != null)
                SchedulerChanged(null, EventArgs.Empty);
        }

        /// <summary>
        /// Called when the account collection changed.
        /// </summary>
        internal static void OnAccountCollectionChanged()
        {
            Trace("EveMonClient.OnAccountCollectionChanged");
            Settings.Save();
            if (AccountCollectionChanged != null)
                AccountCollectionChanged(null, EventArgs.Empty);
        }

        /// <summary>
        /// Called when the monitored characters changed.
        /// </summary>
        internal static void OnMonitoredCharactersChanged()
        {
            Trace("EveMonClient.OnMonitoredCharactersChanged");
            Settings.Save();
            if (MonitoredCharacterCollectionChanged != null)
                MonitoredCharacterCollectionChanged(null, EventArgs.Empty);
        }

        /// <summary>
        /// Called when the character collection changed.
        /// </summary>
        internal static void OnCharacterCollectionChanged()
        {
            Trace("EveMonClient.OnCharacterCollectionChanged");
            Settings.Save();
            if (CharacterCollectionChanged != null)
                CharacterCollectionChanged(null, EventArgs.Empty);
        }

        /// <summary>
        /// Called when the conquerable station list has been updated.
        /// </summary>
        internal static void OnConquerableStationListUpdated()
        {
            Trace("EveMonClient.OnAccountStatusUpdated");
            if (ConquerableStationListUpdated != null)
                ConquerableStationListUpdated(null, EventArgs.Empty);
        }

        /// <summary>
        /// Called when the character list updated.
        /// </summary>
        /// <param name="account">The account.</param>
        internal static void OnCharacterListUpdated(Account account)
        {
            Trace("EveMonClient.OnCharacterListUpdated - {0}", account);
            Settings.Save();
            if (CharacterListUpdated != null)
                CharacterListUpdated(null, EventArgs.Empty);
        }

        /// <summary>
        /// Called when the character sheet updated.
        /// </summary>
        /// <param name="character">The character.</param>
        internal static void OnCharacterUpdated(Character character)
        {
            Trace("EveMonClient.OnCharacterUpdated - {0}", character.Name);
            Settings.Save();
            if (CharacterUpdated != null)
                CharacterUpdated(null, new CharacterChangedEventArgs(character));
        }

        /// <summary>
        /// Called when the character info updated.
        /// </summary>
        /// <param name="character">The character.</param>
        internal static void OnCharacterInfoUpdated(Character character)
        {
            Trace("EveMonClient.OnCharacterInfoUpdated - {0}", character.Name);
            Settings.Save();
            if (CharacterInfoUpdated != null)
                CharacterInfoUpdated(null, new CharacterChangedEventArgs(character));
        }

        /// <summary>
        /// Called when the character skill queue updated.
        /// </summary>
        /// <param name="character">The character.</param>
        internal static void OnCharacterSkillQueueUpdated(Character character)
        {
            Trace("EveMonClient.OnSkillQueueUpdated - {0}", character.Name);
            Settings.Save();
            if (CharacterSkillQueueUpdated != null)
                CharacterSkillQueueUpdated(null, new CharacterChangedEventArgs(character));
        }

        /// <summary>
        /// Called when the character queued skills completed.
        /// </summary>
        /// <param name="character">The character.</param>
        /// <param name="skillsCompleted">The skills completed.</param>
        internal static void OnCharacterQueuedSkillsCompleted(Character character, IEnumerable<QueuedSkill> skillsCompleted)
        {
            Trace("EveMonClient.OnCharacterQueuedSkillsCompleted - {0}", character.Name);
            if (QueuedSkillsCompleted != null)
                QueuedSkillsCompleted(null, new QueuedSkillsEventArgs(character, skillsCompleted));
        }

        /// <summary>
        /// Called when the character standings updated.
        /// </summary>
        /// <param name="character">The character.</param>
        internal static void OnCharacterStandingsUpdated(Character character)
        {
            Trace("EveMonClient.OnCharacterStandingsUpdated - {0}", character.Name);
            Settings.Save();
            if (CharacterStandingsUpdated != null)
                CharacterStandingsUpdated(null, new CharacterChangedEventArgs(character));
        }

        /// <summary>
        /// Called when the character market orders updated.
        /// </summary>
        /// <param name="character">The character.</param>
        internal static void OnCharacterMarketOrdersUpdated(Character character)
        {
            Trace("EveMonClient.OnCharacterMarketOrdersUpdated - {0}", character.Name);
            Settings.Save();
            if (CharacterMarketOrdersUpdated != null)
                CharacterMarketOrdersUpdated(null, new CharacterChangedEventArgs(character));
        }

        /// <summary>
        /// Called when the character industry jobs updated.
        /// </summary>
        /// <param name="character">The character.</param>
        internal static void OnCharacterIndustryJobsUpdated(Character character)
        {
            Trace("EveMonClient.OnCharacterIndustryJobsUpdated - {0}", character.Name);
            Settings.Save();
            if (CharacterIndustryJobsUpdated != null)
                CharacterIndustryJobsUpdated(null, new CharacterChangedEventArgs(character));
        }

        /// <summary>
        /// Called when the character industry jobs completed.
        /// </summary>
        /// <param name="character">The character.</param>
        /// <param name="jobsCompleted">The jobs completed.</param>
        internal static void OnCharacterIndustryJobsCompleted(Character character, IEnumerable<IndustryJob> jobsCompleted)
        {
            Trace("EveMonClient.OnCharacterIndustryJobsCompleted - {0}", character.Name);
            if (CharacterIndustryJobsCompleted != null)
                CharacterIndustryJobsCompleted(null, new IndustryJobsEventArgs(character, jobsCompleted));
        }

        /// <summary>
        /// Called when the character research points updated.
        /// </summary>
        /// <param name="character">The character.</param>
        internal static void OnCharacterResearchPointsUpdated(Character character)
        {
            Trace("EveMonClient.OnCharacterResearchPointsUpdated - {0}", character.Name);
            Settings.Save();
            if (CharacterResearchPointsUpdated != null)
                CharacterResearchPointsUpdated(null, new CharacterChangedEventArgs(character));
        }

        /// <summary>
        /// Called when the character EVE mail messages updated.
        /// </summary>
        /// <param name="character">The character.</param>
        internal static void OnCharacterEVEMailMessagesUpdated(Character character)
        {
            Trace("EveMonClient.OnCharacterEVEMailMessagesUpdated - {0}", character.Name);
            Settings.Save();
            if (CharacterEVEMailMessagesUpdated != null)
                CharacterEVEMailMessagesUpdated(null, new CharacterChangedEventArgs(character));
        }

        /// <summary>
        /// Called when the character EVE mail message body downloaded.
        /// </summary>
        /// <param name="character">The character.</param>
        internal static void OnCharacterEVEMailBodyDownloaded(Character character)
        {
            Trace("EveMonClient.OnCharacterEVEMailBodyDownloaded - {0}", character.Name);
            if (CharacterEVEMailBodyDownloaded != null)
                CharacterEVEMailBodyDownloaded(null, new CharacterChangedEventArgs(character));
        }

        /// <summary>
        /// Called when the character EVE notifications updated.
        /// </summary>
        /// <param name="character">The character.</param>
        internal static void OnCharacterEVENotificationsUpdated(Character character)
        {
            Trace("EveMonClient.OnCharacterEVENotificationsUpdated - {0}", character.Name);
            Settings.Save();
            if (CharacterEVENotificationsUpdated != null)
                CharacterEVENotificationsUpdated(null, new CharacterChangedEventArgs(character));
        }

        /// <summary>
        /// Called when the character EVE notification text downloaded.
        /// </summary>
        /// <param name="character">The character.</param>
        internal static void OnCharacterEVENotificationTextDownloaded(Character character)
        {
            Trace("EveMonClient.OnCharacterEVENotificationTextDownloaded - {0}", character.Name);
            if (CharacterEVENotificationTextDownloaded != null)
                CharacterEVENotificationTextDownloaded(null, new CharacterChangedEventArgs(character));
        }

        /// <summary>
        /// Called when the character portrait updated.
        /// </summary>
        /// <param name="character">The character.</param>
        internal static void OnCharacterPortraitUpdated(Character character)
        {
            Trace("EveMonClient.OnCharacterPortraitUpdated - {0}", character.Name);
            if (CharacterPortraitUpdated != null)
                CharacterPortraitUpdated(null, new CharacterChangedEventArgs(character));
        }

        /// <summary>
        /// Called when the character plan collection changed.
        /// </summary>
        /// <param name="character">The character.</param>
        internal static void OnCharacterPlanCollectionChanged(Character character)
        {
            Trace("EveMonClient.OnCharacterPlanCollectionChanged - {0}", character.Name);
            Settings.Save();
            if (CharacterPlanCollectionChanged != null)
                CharacterPlanCollectionChanged(null, new CharacterChangedEventArgs(character));
        }


        /// <summary>
        /// Called when a plan changed.
        /// </summary>
        /// <param name="plan">The plan.</param>
        internal static void OnPlanChanged(Plan plan)
        {
            Trace("EveMonClient.OnPlanChanged - {0}", plan.Name);
            Settings.Save();
            if (PlanChanged != null)
                PlanChanged(null, new PlanChangedEventArgs(plan));
        }

        /// <summary>
        /// Called when a plan name changed.
        /// </summary>
        /// <param name="plan">The plan.</param>
        internal static void OnPlanNameChanged(Plan plan)
        {
            Trace("EveMonClient.OnPlanNameChanged - {0}", plan.Name);
            Settings.Save();
            if (PlanNameChanged != null)
                PlanNameChanged(null, new PlanChangedEventArgs(plan));
        }

        /// <summary>
        /// Called when the server status updated.
        /// </summary>
        /// <param name="server">The server.</param>
        /// <param name="previousStatus">The previous status.</param>
        /// <param name="status">The status.</param>
        internal static void OnServerStatusUpdated(EveServer server, ServerStatus previousStatus, ServerStatus status)
        {
            Trace("EveMonClient.OnServerStatusUpdated");
            if (ServerStatusUpdated != null)
                ServerStatusUpdated(null, new EveServerEventArgs(server, previousStatus, status));
        }

        /// <summary>
        /// Called when all account characters 'skill in training' check has been updated.
        /// </summary>
        /// <param name="account">The account.</param>
        internal static void OnAccountCharactersSkillInTrainingUpdated(Account account)
        {
            Trace("EveMonClient.OnAccountCharactersSkillInTrainingUpdated - {0}", account);
            if (AccountCharactersSkillInTrainingUpdated != null)
                AccountCharactersSkillInTrainingUpdated(null, EventArgs.Empty);
        }

        /// <summary>
        /// Called when an account status has been updated.
        /// </summary>
        /// <param name="account">The account.</param>
        internal static void OnAccountStatusUpdated(Account account)
        {
            Trace("EveMonClient.OnAccountStatusUpdated - {0}", account);
            if (AccountStatusUpdated != null)
                AccountStatusUpdated(null, EventArgs.Empty);
        }

        /// <summary>
        /// Called when a notification is sent.
        /// </summary>
        /// <param name="notification">The notification.</param>
        internal static void OnNotificationSent(NotificationEventArgs notification)
        {
            Trace("EveMonClient.OnNotificationSent - {0}", notification);
            if (NotificationSent != null)
                NotificationSent(null, notification);
        }

        /// <summary>
        /// Called when a notification gets invalidated.
        /// </summary>
        /// <param name="args">The <see cref="EVEMon.Common.Notifications.NotificationInvalidationEventArgs"/> instance containing the event data.</param>
        internal static void OnNotificationInvalidated(NotificationInvalidationEventArgs args)
        {
            Trace("EveMonClient.OnNotificationInvalidated");
            if (NotificationInvalidated != null)
                NotificationInvalidated(null, args);
        }

        /// <summary>
        /// Called when an update is available.
        /// </summary>
        /// <param name="forumUrl">The forum URL.</param>
        /// <param name="installerUrl">The installer URL.</param>
        /// <param name="updateMessage">The update message.</param>
        /// <param name="currentVersion">The current version.</param>
        /// <param name="newestVersion">The newest version.</param>
        /// <param name="canAutoInstall">if set to <c>true</c> [can auto install].</param>
        /// <param name="installArgs">The install args.</param>
        internal static void OnUpdateAvailable(string forumUrl, string installerUrl, string updateMessage,
                                               Version currentVersion, Version newestVersion,
                                               bool canAutoInstall, string installArgs)
        {
            Trace("EveMonClient.OnUpdateAvailable({0} -> {1}, {2}, {3})",
                  currentVersion, newestVersion, canAutoInstall, installArgs);
            if (UpdateAvailable != null)
                UpdateAvailable(null, new UpdateAvailableEventArgs(forumUrl, installerUrl, updateMessage,
                                                                   currentVersion, newestVersion, canAutoInstall, installArgs));
        }

        /// <summary>
        /// Called when data update is available.
        /// </summary>
        /// <param name="updateUrl">The update URL.</param>
        /// <param name="changedFiles">The changed files.</param>
        internal static void OnDataUpdateAvailable(string updateUrl, List<SerializableDatafile> changedFiles)
        {
            Trace("EveMonClient.OnDataUpdateAvailable(ChangedFiles = {0})", changedFiles.Count);
            if (DataUpdateAvailable != null)
                DataUpdateAvailable(null, new DataUpdateAvailableEventArgs(updateUrl, changedFiles));
        }

        /// <summary>
        /// Called when the BattleClinic API credentials is updated.
        /// </summary>
        /// <param name="errorMessage">The error message.</param>
        internal static void OnBCAPICredentialsUpdated(string errorMessage)
        {
            Trace("EveMonClient.OnBCAPICredentialsUpdated");
            if (BCAPICredentialsUpdated != null)
                BCAPICredentialsUpdated(null, new BCAPIEventArgs(errorMessage));
        }

        #endregion


        #region Diagnostics

        /// <summary>
        /// Sends a message to the trace with the prepended time since startup.
        /// </summary>
        /// <param name="message">message to trace</param>
        public static void Trace(string message)
        {
            TimeSpan time = DateTime.UtcNow.Subtract(s_startTime);
            string timeStr = String.Format(CultureConstants.DefaultCulture,
                                           "{0:#0}d {1:#0}h {2:00}m {3:00}s > ", time.Days, time.Hours, time.Minutes, time.Seconds);
            System.Diagnostics.Trace.WriteLine(timeStr + message);
        }

        /// <summary>
        /// Sends a message to the trace with the prepended time since
        /// startup, in addition to argument inserting into the format.
        /// </summary>
        /// <param name="format"></param>
        /// <param name="args"></param>
        public static void Trace(string format, params object[] args)
        {
            Trace(String.Format(format, args));
        }

        /// <summary>
        /// Sends a message to the trace with the calling method, time
        /// and the types of any arguments passed to the method.
        /// </summary>
        public static void Trace()
        {
            StackTrace stackTrace = new StackTrace();
            StackFrame frame = stackTrace.GetFrame(1);
            MethodBase method = frame.GetMethod();
            string parameters = FormatParameters(method.GetParameters());
            string declaringType = method.DeclaringType.ToString().Replace("EVEMon.", String.Empty);

            Trace("{0}.{1}({2})", declaringType, method.Name, parameters);
        }

        /// <summary>
        /// Formats the parameters of a method into a string.
        /// </summary>
        /// <param name="parameters">The parameters.</param>
        /// <returns>A comma seperated string of paramater types and names.</returns>
        private static string FormatParameters(IEnumerable<ParameterInfo> parameters)
        {
            StringBuilder paramDetail = new StringBuilder();

            foreach (ParameterInfo param in parameters)
            {
                if (paramDetail.Length != 0)
                    paramDetail.Append(", ");

                paramDetail.AppendFormat("{0} {1}", param.GetType().Name, param.Name);
            }

            return paramDetail.ToString();
        }

        /// <summary>
        /// Starts the logging of trace messages to a file.
        /// </summary>
        public static void StartTraceLogging()
        {
            try
            {
                System.Diagnostics.Trace.AutoFlush = true;
                s_traceStream = File.CreateText(TraceFileNameFullPath);
                s_traceListener = new TextWriterTraceListener(s_traceStream);
                System.Diagnostics.Trace.Listeners.Add(s_traceListener);
            }
            catch (IOException e)
            {
                string text = String.Format("EVEMon has encountered an error and needs to terminate.{0}" +
                                            "The error message is:{0}{0}\"{1}\"",
                                            Environment.NewLine, e.Message);
                MessageBox.Show(text, "EVEMon Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Application.Exit();
            }
        }

        /// <summary>
        /// Stops the logging of trace messages to a file.
        /// </summary>
        public static void StopTraceLogging()
        {
            System.Diagnostics.Trace.Listeners.Remove(s_traceListener);
            s_traceListener.Close();
            s_traceStream.Close();
        }

        /// <summary>
        /// Will only execute if DEBUG is set, thus lets us avoid #IFDEF.
        /// </summary>
        [Conditional("DEBUG")]
        public static void CheckIsDebug()
        {
            IsDebugBuild = true;
        }

        #endregion
    }
}