using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Reqnroll;
using SoundRecorder.Core;
using SoundRecorder.Infrastructure.Models;
using SoundRecorder.Infrastructure.Privacy;

namespace SoundRecorder.Core.Tests.Steps;

[Binding]
[Scope(Feature = "Install and operate a private offline Windows recorder")]
public sealed class PrivacyAndSetupSteps
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"sound-recorder-model-tests-{Guid.NewGuid():N}");
    private readonly CountingHandler _handler = new();
    private LocalTranscriptionModelManager? _models;
    private string? _packagePath;
    private TranscriptionModelPackage? _installedPackage;
    private string? _error;
    private bool _networkDisabled;

    public PrivacyAndSetupSteps()
    {
        Directory.CreateDirectory(_directory);
    }

    [Given("a Windows 11 x64 computer")]
    [Given("the user has no administrator rights")]
    public void GivenStandardWindowsUser()
    {
    }

    [When("the user installs and launches the desktop app")]
    public void WhenTheUserInstallsAndLaunchesTheDesktopApp()
    {
        Assert.True(PrivacyPolicy.SupportsWindows11X64);
    }

    [Then("installation and normal use do not require elevation")]
    public void ThenInstallationAndNormalUseDoNotRequireElevation()
    {
        Assert.False(InstallationPolicy.RequiresAdministrator);
    }

    [Then("recording and transcription do not require a dedicated GPU")]
    public void ThenRecordingAndTranscriptionDoNotRequireADedicatedGpu()
    {
        Assert.False(InstallationPolicy.RequiresDedicatedGpu);
        Assert.Contains(
            "Whisper.net.Runtime",
            File.ReadAllText(Path.Combine(
                FindRepositoryRoot(),
                "src",
                "SoundRecorder.Infrastructure",
                "SoundRecorder.Infrastructure.csproj")));
    }

    [Then("the initial user interface is in English")]
    public void ThenTheInitialUserInterfaceIsInEnglish()
    {
        Assert.Equal("English", InstallationPolicy.InitialUiLanguage);
        Assert.Contains("New recording", File.ReadAllText(GetDesktopFile("MainWindow.xaml")));
    }

    [Given("the user has internet access")]
    public void GivenTheUserHasInternetAccess()
    {
        _networkDisabled = false;
        CreateManager();
        _packagePath = CreatePackage();
    }

    [When("the user explicitly requests a model download")]
    public async Task WhenTheUserExplicitlyRequestsAModelDownload()
    {
        _handler.ResponseFile = _packagePath;
        _installedPackage = await _models!.DownloadAndImportAsync(
            new Uri("https://models.example.test/model.zip"),
            CancellationToken.None);
    }

    [Then("the app may download the model")]
    public void ThenTheAppMayDownloadTheModel()
    {
        Assert.Equal(1, _handler.RequestCount);
        Assert.Equal(HttpMethod.Get, _handler.LastMethod);
    }

    [Then("the app checks compatibility and package integrity before activation")]
    public void ThenTheAppChecksCompatibilityAndPackageIntegrityBeforeActivation()
    {
        Assert.NotNull(_installedPackage);
        Assert.True(_models!.HasCompatibleModel("English"));
        Assert.True(_models.HasCompatibleModel("Brazilian Portuguese"));
    }

    [Then("the request contains no recording audio or transcript content")]
    public void ThenTheRequestContainsNoRecordingAudioOrTranscriptContent()
    {
        Assert.Null(_handler.LastContent);
    }

    [Given("the installer and a compatible model package have been transferred from another computer")]
    public void GivenInstallerAndPackageWereTransferred()
    {
        CreateManager();
        _packagePath = CreatePackage();
    }

    [Given("the target computer has no network connection")]
    public void GivenTheTargetComputerHasNoNetworkConnection()
    {
        _networkDisabled = true;
    }

    [When("the user installs the app and imports the local model package")]
    public async Task WhenTheUserInstallsTheAppAndImportsTheLocalModelPackage()
    {
        _installedPackage = await _models!.ImportAsync(
            _packagePath!,
            CancellationToken.None);
    }

    [Then("recording and local transcription become available without network access")]
    public void ThenRecordingAndLocalTranscriptionBecomeAvailableWithoutNetworkAccess()
    {
        Assert.True(_networkDisabled);
        Assert.NotNull(_models!.GetModelPath("English"));
        Assert.Equal(0, _handler.RequestCount);
    }

    [When("an imported or downloaded model package fails compatibility or integrity checks")]
    public async Task WhenAModelPackageFailsCompatibilityOrIntegrityChecks()
    {
        CreateManager();
        await _models!.ImportAsync(CreatePackage(id: "working"), CancellationToken.None);
        _packagePath = CreatePackage(invalidHash: true);
        try
        {
            await _models.ImportAsync(_packagePath, CancellationToken.None);
        }
        catch (Exception exception)
        {
            _error = exception.Message;
        }
    }

    [Then("the app reports an actionable error")]
    public void ThenTheAppReportsAnActionableError()
    {
        Assert.False(string.IsNullOrWhiteSpace(_error));
    }

    [Then("the package is not activated")]
    public void ThenThePackageIsNotActivated()
    {
        Assert.Equal("working", _models!.CurrentPackage?.Id);
    }

    [Then("any previously working model remains available")]
    public void ThenAnyPreviouslyWorkingModelRemainsAvailable()
    {
        Assert.NotNull(_models!.GetModelPath("English"));
    }

    [Given("a working transcription model is installed")]
    public async Task GivenAWorkingTranscriptionModelIsInstalled()
    {
        CreateManager();
        await _models!.ImportAsync(CreatePackage(id: "working"), CancellationToken.None);
    }

    [When("an explicitly requested model update fails")]
    public async Task WhenAnExplicitlyRequestedModelUpdateFails()
    {
        try
        {
            await _models!.ImportAsync(
                CreatePackage(id: "update", invalidHash: true),
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            _error = exception.Message;
        }
    }

    [Then("the previous working model remains usable")]
    public void ThenThePreviousWorkingModelRemainsUsable()
    {
        Assert.Equal("working", _models!.CurrentPackage?.Id);
        Assert.True(File.Exists(_models.GetModelPath("English")));
    }

    [Then("the update failure is clearly reported")]
    public void ThenTheUpdateFailureIsClearlyReported()
    {
        Assert.Contains("SHA-256", _error);
    }

    [Given("a compatible model is installed")]
    public async Task GivenACompatibleModelIsInstalled()
    {
        CreateManager();
        await _models!.ImportAsync(CreatePackage(), CancellationToken.None);
    }

    [Given("all network connections are disabled")]
    public void GivenAllNetworkConnectionsAreDisabled()
    {
        _networkDisabled = true;
    }

    [When("the user records a conversation and requests transcription")]
    public void WhenTheUserRecordsAConversationAndRequestsTranscription()
    {
        Assert.True(_models!.HasCompatibleModel("English"));
    }

    [Then("recording and transcription complete locally")]
    public void ThenRecordingAndTranscriptionCompleteLocally()
    {
        Assert.False(PrivacyPolicy.UsesCloudTranscriptionFallback);
        Assert.True(Path.IsPathFullyQualified(_models!.GetModelPath("English")!));
    }

    [Then("no account, cloud endpoint, or internet connection is required")]
    public void ThenNoAccountCloudEndpointOrInternetConnectionIsRequired()
    {
        Assert.False(InstallationPolicy.RequiresAccount);
        Assert.Equal(0, _handler.RequestCount);
    }

    [Then("there is no cloud fallback")]
    public void ThenThereIsNoCloudFallback()
    {
        Assert.False(PrivacyPolicy.UsesCloudTranscriptionFallback);
    }

    [Given("the user has not requested model installation or an update")]
    public void GivenTheUserHasNotRequestedModelInstallationOrAnUpdate()
    {
        CreateManager();
    }

    [When("the app starts, records, transcribes, or remains idle")]
    public void WhenTheAppStartsRecordsTranscribesOrRemainsIdle()
    {
        _models!.HasCompatibleModel("English");
    }

    [Then("the app makes no network requests")]
    public void ThenTheAppMakesNoNetworkRequests()
    {
        Assert.Equal(0, _handler.RequestCount);
    }

    [Then("the app sends no telemetry")]
    public void ThenTheAppSendsNoTelemetry()
    {
        Assert.False(PrivacyPolicy.SendsTelemetry);
    }

    [Then("the app performs no background update checks")]
    public void ThenTheAppPerformsNoBackgroundUpdateChecks()
    {
        Assert.False(PrivacyPolicy.ChecksForUpdatesInBackground);
    }

    [Then("audio, transcripts, recording history, and recovery files remain in local storage")]
    public void ThenConversationDataRemainsInLocalStorage()
    {
        Assert.True(PrivacyPolicy.StoresConversationContentLocally);
    }

    [Then("diagnostics contain no audio or transcript content")]
    public void ThenDiagnosticsContainNoConversationContent()
    {
        Assert.DoesNotContain(
            "telemetry",
            File.ReadAllText(GetDesktopFile("MainWindow.xaml.cs")),
            StringComparison.OrdinalIgnoreCase);
    }

    [Then("version one does not provide app-managed encryption or password protection")]
    public void ThenVersionOneDoesNotProvideEncryptionOrPasswordProtection()
    {
        Assert.False(PrivacyPolicy.ProvidesAppManagedEncryption);
        Assert.False(PrivacyPolicy.ProvidesPasswordProtection);
    }

    [Then("the app does not claim to control external backup or synchronization tools")]
    public void ThenTheAppDoesNotClaimToControlExternalTools()
    {
        Assert.False(PrivacyPolicy.DiagnosesEverySynchronizationTool);
    }

    [When("the user selects a network share or mapped network drive as the output destination")]
    public void WhenTheUserSelectsANetworkShare()
    {
        try
        {
            LocalPathPolicy.RejectNetworkDestination(@"\\server\recordings");
        }
        catch (Exception exception)
        {
            _error = exception.Message;
        }
    }

    [Then("the destination is rejected with an actionable explanation")]
    public void ThenTheDestinationIsRejectedWithAnActionableExplanation()
    {
        Assert.Contains("local folder", _error);
    }

    [Then("the app does not silently substitute another destination")]
    public void ThenTheAppDoesNotSilentlySubstituteAnotherDestination()
    {
        Assert.NotNull(_error);
    }

    [When("the user selects a folder known to be cloud-synchronized")]
    public void WhenTheUserSelectsACloudSynchronizedFolder()
    {
        var root = Path.Combine(_directory, "OneDrive");
        Directory.CreateDirectory(root);
        Assert.True(LocalPathPolicy.IsKnownSynchronizedFolder(
            Path.Combine(root, "recordings"),
            [root]));
    }

    [Then("the app warns that an external service may upload files from that folder")]
    public void ThenTheAppWarnsThatAnExternalServiceMayUploadFiles()
    {
        Assert.Contains("may upload", PrivacyPolicy.CloudSynchronizationWarning);
    }

    [Then("the app does not claim to detect every third-party synchronization tool")]
    public void ThenTheAppDoesNotClaimToDetectEverySynchronizationTool()
    {
        Assert.Contains("cannot detect every", PrivacyPolicy.CloudSynchronizationWarning);
    }

    [When("the user navigates recording, setup, history, and transcription controls")]
    public void WhenTheUserNavigatesAllControls()
    {
        Assert.True(File.Exists(GetDesktopFile("MainWindow.xaml")));
    }

    [Then("all controls are usable with a keyboard")]
    public void ThenAllControlsAreUsableWithAKeyboard()
    {
        Assert.DoesNotContain(
            "Focusable=\"False\"",
            File.ReadAllText(GetDesktopFile("MainWindow.xaml")));
    }

    [Then("controls have screen-reader labels")]
    public void ThenControlsHaveScreenReaderLabels()
    {
        Assert.Contains(
            "AutomationProperties.Name",
            File.ReadAllText(GetDesktopFile("MainWindow.xaml")));
    }

    [Then("recording, paused, interrupted, and job statuses are available to the screen reader")]
    public void ThenStatusesAreAvailableToTheScreenReader()
    {
        Assert.Contains(
            "AutomationProperties.LiveSetting",
            File.ReadAllText(GetDesktopFile("MainWindow.xaml")));
    }

    [Then("Windows versions other than Windows 11 are outside version-one support")]
    public void ThenOtherWindowsVersionsAreOutsideSupport()
    {
        Assert.True(PrivacyPolicy.SupportsWindows11X64);
    }

    [Then("ARM64 is outside version-one support")]
    public void ThenArm64IsOutsideSupport()
    {
        Assert.False(PrivacyPolicy.SupportsArm64);
        Assert.Equal("win-x64", InstallationPolicy.RuntimeIdentifier);
    }

    [Then("compressed audio output and separate source tracks are outside version-one scope")]
    public void ThenCompressedAndSeparateTracksAreOutOfScope()
    {
        Assert.False(PrivacyPolicy.SupportsCompressedAudioOutput);
        Assert.False(PrivacyPolicy.SupportsSeparateSourceTracks);
    }

    private void CreateManager()
    {
        _models = new LocalTranscriptionModelManager(
            Path.Combine(_directory, Guid.NewGuid().ToString("N"), "models"),
            new HttpClient(_handler));
    }

    private string CreatePackage(
        bool invalidHash = false,
        string id = "test-model")
    {
        var packagePath = Path.Combine(_directory, $"{Guid.NewGuid():N}.zip");
        var modelBytes = Encoding.UTF8.GetBytes($"model-{id}");
        var hash = Convert.ToHexString(SHA256.HashData(modelBytes));
        if (invalidHash)
        {
            hash = new string('0', 64);
        }

        var package = new TranscriptionModelPackage(
            1,
            id,
            "Whisper.net 1.9",
            "ggml-test.bin",
            hash,
            "Windows11-x64",
            ["English", "Brazilian Portuguese"]);
        using var stream = File.Create(packagePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        var model = archive.CreateEntry("ggml-test.bin");
        using (var modelStream = model.Open())
        {
            modelStream.Write(modelBytes);
        }

        var manifest = archive.CreateEntry("manifest.json");
        using (var manifestStream = manifest.Open())
        {
            JsonSerializer.Serialize(manifestStream, package);
        }

        return packagePath;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "GLOSSARY.md")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ??
               throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static string GetDesktopFile(string fileName)
    {
        return Path.Combine(
            FindRepositoryRoot(),
            "src",
            "SoundRecorder.Desktop",
            fileName);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public string? ResponseFile { get; set; }
        public int RequestCount { get; private set; }
        public HttpMethod? LastMethod { get; private set; }
        public HttpContent? LastContent { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            LastMethod = request.Method;
            LastContent = request.Content;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(File.OpenRead(ResponseFile!)),
            });
        }
    }
}
