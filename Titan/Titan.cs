﻿using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using CommandLine;
using Newtonsoft.Json;
using Quartz;
using Quartz.Impl;
using Serilog;
using Serilog.Core;
using Titan.Bans;
using Titan.Bootstrap;
using Titan.Bootstrap.Verbs;
using Titan.Logging;
using Titan.Managers;
using Titan.Meta;
using Titan.Restrictions;
using Titan.UI;
using Titan.Util;
using Titan.Web;

namespace Titan
{
    public sealed class Titan
    {

        public static Logger Logger; // Global logger
        public static Titan Instance;

        public Options Options;
        public bool EnableUI = true;
        public object ParsedObject;

        public AccountManager AccountManager;
        public ThreadManager ThreadManager;
        public VictimTracker VictimTracker;
        public UIManager UIManager;

        public JsonSerializer JsonSerializer;
        public SWAHandle WebHandle;

        public bool DummyMode = false;
        public IScheduler Scheduler;

        public DirectoryInfo DebugDirectory = new DirectoryInfo(Path.Combine(Environment.CurrentDirectory, "debug"));

        [STAThread]
        public static int Main(string[] args)
        {
            Thread.CurrentThread.Name = "Main";

            Instance = new Titan
            {
                Options = new Options()
            };

            Logger = LogCreator.Create();
            
            // Workaround for Mono related issue regarding System.Net.Http.
            // More detail: https://github.com/dotnet/corefx/issues/19914

            var systemNetHttpDll = new FileInfo(Path.Combine(Environment.CurrentDirectory, "System.Net.Http.dll"));
            
            if (systemNetHttpDll.Exists && !PlatformUtil.IsWindows())
            {
                systemNetHttpDll.Delete();
            }
            
            Logger.Debug("Startup: Loading Serilog <-> Common Logging Bridge.");
            
            // Common Logging <-> Serilog bridge
            Log.Logger = LogCreator.Create("Quartz.NET Scheduler");
            
            Logger.Debug("Startup: Loading Quartz.NET.");
            
            // Quartz.NET
            Instance.Scheduler = StdSchedulerFactory.GetDefaultScheduler().Result;
            Instance.Scheduler.Start();

            Logger.Debug("Startup: Parsing Command Line Arguments.");

            Parser.Default.ParseArguments<Options, ReportOptions, CommendOptions, IdleOptions>(args)
                .WithParsed<ReportOptions>(options =>
                {
                    Instance.EnableUI = false;
                    Instance.ParsedObject = options;
                })
                .WithParsed<CommendOptions>(options =>
                {
                    Instance.EnableUI = false;
                    Instance.ParsedObject = options;
                })
                .WithParsed<IdleOptions>(options =>
                {
                    Instance.EnableUI = false;
                    Instance.ParsedObject = options;
                })
                .WithNotParsed(error =>
                {
                    Instance.EnableUI = true;
                    Logger.Information("No valid verb has been provided while parsing. Opening UI...");
                });
            
            // Reinitialize logger with new parsed debug option
            Logger = LogCreator.Create();

            if(Instance.Options.Debug)
            {
                if(!Instance.DebugDirectory.Exists)
                {
                    Instance.DebugDirectory.Create();
                }
            }

            if (Instance.Options.DisableBlacklist)
            {
                Logger.Debug("Blacklist has been disabled by passing the --noblacklist option.");
            }

            Logger.Debug("Startup: Loading UI Manager, Victim Tracker, Account Manager and Ban Manager.");

            Instance.JsonSerializer = new JsonSerializer();
            
            try
            {
                Instance.UIManager = new UIManager();
            }
            catch (Exception ex)
            {
                if (ex.GetType() == typeof(InvalidOperationException))
                {
                    var osEx = (InvalidOperationException) ex;

                    if (osEx.Message.ToLower().Contains("could not detect platform"))
                    {
                        Log.Error("---------------------------------------");
                        Log.Error("A fatal error has been detected!");
                        Log.Error("You are missing a Eto.Forms platform assembly.");
                        if (Type.GetType("Mono.Runtime") != null)
                        {
                            Log.Error("Please read the README.md file and install all required dependencies.");
                        }
                        Log.Error("Either {0} or {1} Titan. Titan will now shutdown.", "redownload", "rebuild");
                        Log.Error("Contact {Marc} on Discord for more information.", "Marc3842h#7312");
                        Log.Error("---------------------------------------");

                        return -1;
                    }
                }
                
                Log.Error(ex, "A error occured while loading UI.");
                throw;
            }

            Instance.VictimTracker = new VictimTracker();
            
            Instance.Scheduler.ScheduleJob(Instance.VictimTracker.Job, Instance.VictimTracker.Trigger);

            Instance.AccountManager = new AccountManager(new FileInfo(
                Path.Combine(Environment.CurrentDirectory, Instance.Options.AccountsFile))
            );

            Instance.ThreadManager = new ThreadManager();

            Instance.WebHandle = new SWAHandle();
            
            Logger.Debug("Startup: Registering Shutdown Hook.");

            AppDomain.CurrentDomain.ProcessExit += OnShutdown;

            Logger.Debug("Startup: Parsing accounts.json file.");

            Instance.AccountManager.ParseAccountFile(); 
            
            Logger.Debug("Startup: Initializing Forms...");

            Instance.UIManager.InitializeForms();
            
            // Load after Forms were initialized
            Instance.WebHandle.Load();

            Logger.Information("Hello and welcome to Titan v1.6.0-EAP.");

            if (Instance.EnableUI || Instance.ParsedObject == null || Instance.DummyMode)
            {
                Instance.UIManager.ShowForm(UIType.General);
            }
            else
            {
                if (Instance.ParsedObject.GetType() == typeof(ReportOptions))
                {
                    var opt = (ReportOptions) Instance.ParsedObject;

                    var steamID = SteamUtil.Parse(opt.Target);
                    if (Blacklist.IsBlacklisted(steamID))
                    {
                        Instance.UIManager.SendNotification(
                            "Restriction applied",
                            "The target you are trying to report is blacklisted from botting " +
                            "in Titan.",
                            delegate { Process.Start("https://github.com/Marc3842h/Titan/wiki/Blacklist"); }
                        );
                    }
                    else
                    {
                        Instance.AccountManager.StartReporting(Instance.AccountManager.Index,
                            new ReportInfo
                            {
                                SteamID = SteamUtil.Parse(opt.Target),
                                MatchID = SharecodeUtil.Parse(opt.Match),

                                AbusiveText = opt.AbusiveTextChat,
                                AbusiveVoice = opt.AbusiveVoiceChat,
                                Griefing = opt.Griefing,
                                AimHacking = opt.AimHacking,
                                WallHacking = opt.WallHacking,
                                OtherHacking = opt.OtherHacking
                            });
                    }
                }
                else if (Instance.ParsedObject.GetType() == typeof(CommendOptions))
                {
                    var opt = (CommendOptions) Instance.ParsedObject;

                    Instance.AccountManager.StartCommending(Instance.AccountManager.Index,
                        new CommendInfo
                        {
                            SteamID = SteamUtil.Parse(opt.Target),

                            Friendly = opt.Friendly,
                            Leader = opt.Leader,
                            Teacher = opt.Teacher
                        });
                }
                else if (Instance.ParsedObject.GetType() == typeof(IdleOptions))
                {
                    var opt = (IdleOptions) Instance.ParsedObject;

                    // TODO: Parse the idle options as soon as idling is implemented
                }
                else
                {
                    Instance.UIManager.ShowForm(UIType.General);
                }
            }

            Instance.UIManager.StartMainLoop();
            
            // The Shutdown handler gets only called after the last thread finished.
            // Quartz runs a Watchdog until Scheduler#Shutdown is called, so we're calling it
            // before Titan will be calling the Shutdown Hook.
            Logger.Debug("Shutdown: Shutting down Quartz.NET Scheduler.");
            
            Instance.Scheduler.Shutdown();
            
            return 0; // OK.
        }

        public static void OnShutdown(object sender, EventArgs args)
        {
            // Check if Titan got closed via Process Manager or by the TrayIcon
            if(!Instance.Scheduler.IsShutdown)
            {
                Instance.Scheduler.Shutdown();
            }
            
            Instance.AccountManager.SaveAccountsFile();
            Instance.VictimTracker.SaveVictimsFile();
            Instance.WebHandle.Save();
            Instance.AccountManager.SaveIndexFile();

            Logger.Information("Thank you and have a nice day.");

            Log.CloseAndFlush();
        }

    }
}