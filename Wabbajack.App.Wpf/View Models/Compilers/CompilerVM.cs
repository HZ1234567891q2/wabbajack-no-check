﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive;
using Microsoft.Extensions.Logging;
using Wabbajack.Messages;
using ReactiveUI;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using DynamicData;
using Microsoft.WindowsAPICodePack.Dialogs;
using ReactiveUI.Fody.Helpers;
using Wabbajack.Common;
using Wabbajack.Compiler;
using Wabbajack.DTOs;
using Wabbajack.DTOs.JsonConverters;
using Wabbajack.Models;
using Wabbajack.Networking.WabbajackClientApi;
using Wabbajack.Paths;
using Wabbajack.Paths.IO;
using Wabbajack.Services.OSIntegrated;

namespace Wabbajack
{
    
    
    public enum CompilerState
    {
        Configuration,
        Compiling,
        Completed,
        Errored
    }
    public class CompilerVM : BackNavigatingVM, ICpuStatusVM
    {
        private const string LastSavedCompilerSettings = "last-saved-compiler-settings";
        private readonly DTOSerializer _dtos;
        private readonly SettingsManager _settingsManager;
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<CompilerVM> _logger;
        private readonly ResourceMonitor _resourceMonitor;
        private readonly CompilerSettingsInferencer _inferencer;
        private readonly Client _wjClient;

        [Reactive]
        public CompilerState State { get; set; }
        
        [Reactive]
        public ISubCompilerVM SubCompilerVM { get; set; }
        
        // Paths 
        public FilePickerVM ModlistLocation { get; }
        public FilePickerVM DownloadLocation { get; }
        public FilePickerVM OutputLocation { get; }
        
        // Modlist Settings
        
        [Reactive] public string ModListName { get; set; }
        [Reactive] public string Version { get; set; }
        [Reactive] public string Author { get; set; }
        [Reactive] public string Description { get; set; }
        public FilePickerVM ModListImagePath { get; } = new();
        [Reactive] public ImageSource ModListImage { get; set; }
        [Reactive] public string Website { get; set; }
        [Reactive] public string Readme { get; set; }
        [Reactive] public bool IsNSFW { get; set; }
        [Reactive] public bool PublishUpdate { get; set; }
        [Reactive] public string MachineUrl { get; set; }
        [Reactive] public Game BaseGame { get; set; }
        [Reactive] public string SelectedProfile { get; set; }
        [Reactive] public AbsolutePath GamePath { get; set; }
        [Reactive] public bool IsMO2Compilation { get; set; }

        [Reactive] public RelativePath[] AlwaysEnabled { get; set; } = Array.Empty<RelativePath>();
        [Reactive] public RelativePath[] NoMatchInclude { get; set; } = Array.Empty<RelativePath>();
        
        [Reactive] public string[] OtherProfiles { get; set; } = Array.Empty<string>();
        
        [Reactive] public AbsolutePath Source { get; set; }
        
        public AbsolutePath SettingsOutputLocation => Source.Combine(ModListName).WithExtension(Ext.CompilerSettings);
        
        
        public ReactiveCommand<Unit, Unit> ExecuteCommand { get; }

        public LogStream LoggerProvider { get; }
        public ReadOnlyObservableCollection<CPUDisplayVM> StatusList => _resourceMonitor.Tasks;
        
        [Reactive]
        public ErrorResponse ErrorState { get; private set; }
        
        public CompilerVM(ILogger<CompilerVM> logger, DTOSerializer dtos, SettingsManager settingsManager,
            IServiceProvider serviceProvider, LogStream loggerProvider, ResourceMonitor resourceMonitor, 
            CompilerSettingsInferencer inferencer, Client wjClient) : base(logger)
        {
            _logger = logger;
            _dtos = dtos;
            _settingsManager = settingsManager;
            _serviceProvider = serviceProvider;
            LoggerProvider = loggerProvider;
            _resourceMonitor = resourceMonitor;
            _inferencer = inferencer;
            _wjClient = wjClient;

            BackCommand =
                ReactiveCommand.CreateFromTask(async () =>
                {
                    await SaveSettingsFile();
                    NavigateToGlobal.Send(NavigateToGlobal.ScreenType.ModeSelectionView);
                });
            
            SubCompilerVM = new MO2CompilerVM(this);

            ExecuteCommand = ReactiveCommand.CreateFromTask(async () => await StartCompilation());

            ModlistLocation = new FilePickerVM
            {
                ExistCheckOption = FilePickerVM.CheckOptions.On,
                PathType = FilePickerVM.PathTypeOptions.File,
                PromptTitle = "Select a config file or a modlist.txt file"
            };

            DownloadLocation = new FilePickerVM
            {
                ExistCheckOption = FilePickerVM.CheckOptions.On,
                PathType = FilePickerVM.PathTypeOptions.Folder,
                PromptTitle = "Location where the downloads for this list are stored"
            };
            
            OutputLocation = new FilePickerVM
            {
                ExistCheckOption = FilePickerVM.CheckOptions.Off,
                PathType = FilePickerVM.PathTypeOptions.File,
                PromptTitle = "Location where the compiled modlist will be stored"
            };
            OutputLocation.Filters.Add(new CommonFileDialogFilter(".wabbajack", "*.wabbajack"));
            
            ModlistLocation.Filters.AddRange(new []
            {
                new CommonFileDialogFilter("MO2 Modlist", "*" + Ext.Txt),
                new CommonFileDialogFilter("Compiler Settings File", "*" + Ext.CompilerSettings)
            });

            
            this.WhenActivated(disposables =>
            {
                State = CompilerState.Configuration;
                Disposable.Empty.DisposeWith(disposables);

                ModlistLocation.WhenAnyValue(vm => vm.TargetPath)
                    .Subscribe(p => InferModListFromLocation(p).FireAndForget())
                    .DisposeWith(disposables);


                this.WhenAnyValue(x => x.DownloadLocation.TargetPath)
                    .CombineLatest(this.WhenAnyValue(x => x.ModlistLocation.TargetPath),
                        this.WhenAnyValue(x => x.OutputLocation.TargetPath),
                        this.WhenAnyValue(x => x.DownloadLocation.ErrorState),
                        this.WhenAnyValue(x => x.ModlistLocation.ErrorState),
                        this.WhenAnyValue(x => x.OutputLocation.ErrorState),
                        this.WhenAnyValue(x => x.ModListName),
                        this.WhenAnyValue(x => x.Version))
                    .Select(_ => Validate())
                    .BindToStrict(this, vm => vm.ErrorState)
                    .DisposeWith(disposables);
                
                LoadLastSavedSettings().FireAndForget();
            });
        }

        private ErrorResponse Validate()
        {
            var errors = new List<ErrorResponse>();
            errors.Add(DownloadLocation.ErrorState);
            errors.Add(ModlistLocation.ErrorState);
            errors.Add(OutputLocation.ErrorState);
            return ErrorResponse.Combine(errors);
        }

        private async Task InferModListFromLocation(AbsolutePath path)
        {
            using var _ = LoadingLock.WithLoading();
            if (path == default || path.FileName != "modlist.txt".ToRelativePath())
                return;
            
            var settings = await _inferencer.InferModListFromLocation(path);
            if (settings == null) return;

            BaseGame = settings.Game;
            ModListName = settings.ModListName;
            Source = settings.Source;
            DownloadLocation.TargetPath = settings.Downloads;
            OutputLocation.TargetPath = settings.OutputFile;
            SelectedProfile = settings.Profile;
            OtherProfiles = settings.AdditionalProfiles;
            AlwaysEnabled = settings.AlwaysEnabled;
            NoMatchInclude = settings.NoMatchInclude;
        }


        private async Task StartCompilation()
        {
            var tsk = Task.Run(async () =>
            {
                try
                {
                    var token = CancellationToken.None;
                    State = CompilerState.Compiling;

                    var mo2Settings = new CompilerSettings
                    {
                        Game = BaseGame,
                        ModListName = ModListName,
                        ModListAuthor = Author,
                        ModlistReadme = Readme,
                        Source = Source,
                        Downloads = DownloadLocation.TargetPath,
                        OutputFile = OutputLocation.TargetPath,
                        Profile = SelectedProfile,
                        AdditionalProfiles = OtherProfiles,
                        AlwaysEnabled = AlwaysEnabled,
                        NoMatchInclude = NoMatchInclude,
                        UseGamePaths = true
                    };

                    if (PublishUpdate && !await RunPreflightChecks(token))
                    {
                        State = CompilerState.Errored;
                        return;
                    }

                    var compiler = MO2Compiler.Create(_serviceProvider, mo2Settings);

                    await compiler.Begin(token);

                    if (PublishUpdate)
                    {
                        _logger.LogInformation("Publishing List");
                        var downloadMetadata = _dtos.Deserialize<DownloadMetadata>(
                            await mo2Settings.OutputFile.WithExtension(Ext.Meta).WithExtension(Ext.Json).ReadAllTextAsync())!;
                        await _wjClient.PublishModlist(MachineUrl, System.Version.Parse(Version), mo2Settings.OutputFile, downloadMetadata);
                    }
                    _logger.LogInformation("Compiler Finished");

                    State = CompilerState.Completed;
                }
                catch (Exception ex)
                {
                    State = CompilerState.Errored;
                    _logger.LogInformation(ex, "Failed Compilation : {Message}", ex.Message);
                }
            });

            await tsk;
        }

        private async Task<bool> RunPreflightChecks(CancellationToken token)
        {
            var lists = await _wjClient.GetMyModlists(token);
            if (!lists.Any(x => x.Equals(MachineUrl, StringComparison.InvariantCultureIgnoreCase)))
            {
                _logger.LogError("Preflight Check failed, list {MachineUrl} not found in any repository", MachineUrl);
                return false;
            }

            if (!System.Version.TryParse(Version, out var v))
            {
                _logger.LogError("Bad Version Number {Version}", Version);
                return false;
            }

            return true;
        }

        private async Task SaveSettingsFile()
        {
            if (Source == default) return;
            await using var st = SettingsOutputLocation.Open(FileMode.Create, FileAccess.Write, FileShare.None);
            await JsonSerializer.SerializeAsync(st, GetSettings(), _dtos.Options);

            await _settingsManager.Save(LastSavedCompilerSettings, Source);
        }

        private async Task LoadLastSavedSettings()
        {
            var lastPath = await _settingsManager.Load<AbsolutePath>(LastSavedCompilerSettings);
            if (Source == default) return;
            Source = lastPath;
        }

                    
        private CompilerSettings GetSettings()
        {
            return new CompilerSettings
            {
                ModListName = ModListName,
                ModListAuthor = Author,
                Downloads = DownloadLocation.TargetPath,
                Source = ModlistLocation.TargetPath,
                Game = BaseGame,
                Profile = SelectedProfile,
                UseGamePaths = true,
                OutputFile = OutputLocation.TargetPath.Combine(SelectedProfile).WithExtension(Ext.Wabbajack),
                AlwaysEnabled = AlwaysEnabled,
                AdditionalProfiles = OtherProfiles,
                NoMatchInclude = NoMatchInclude,
            };
        }

        #region ListOps

        public void AddOtherProfile(string profile)
        {
            OtherProfiles = (OtherProfiles ?? Array.Empty<string>()).Append(profile).Distinct().ToArray();
        }

        public void RemoveProfile(string profile)
        {
            OtherProfiles = OtherProfiles.Where(p => p != profile).ToArray();
        }
        
        public void AddAlwaysEnabled(RelativePath path)
        {
            AlwaysEnabled = (AlwaysEnabled ?? Array.Empty<RelativePath>()).Append(path).Distinct().ToArray();
        }

        public void RemoveAlwaysEnabled(RelativePath path)
        {
            AlwaysEnabled = AlwaysEnabled.Where(p => p != path).ToArray();
        }
        
        public void AddNoMatchInclude(RelativePath path)
        {
            NoMatchInclude = (NoMatchInclude ?? Array.Empty<RelativePath>()).Append(path).Distinct().ToArray();
        }

        public void RemoveNoMatchInclude(RelativePath path)
        {
            NoMatchInclude = NoMatchInclude.Where(p => p != path).ToArray();
        }

        #endregion
    }
}
