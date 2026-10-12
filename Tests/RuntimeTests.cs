using Editor.Android;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace AndroidExportTests;

[TestClass]
public class RuntimeBundleTests
{
	[TestMethod]
	public void MissingDirectory()
	{
		var bundle = new RuntimeBundle( "/definitely/not/here", "sbox", false );
		Assert.IsFalse( bundle.IsValid );
		StringAssert.Contains( bundle.Errors.Single(), "doesn't exist" );

		Assert.IsFalse( new RuntimeBundle( null, "sbox", false ).IsValid );
		Assert.ThrowsException<InvalidOperationException>( () => bundle.Entries().ToList() );
	}

	[TestMethod]
	public void RequiresTheArm64Folder()
	{
		using var dir = new TempDir();
		dir.File( "lib/armeabi-v7a/libsbox.so" ); // wrong ABI doesn't count

		var bundle = new RuntimeBundle( dir.Path, "sbox", false );
		Assert.IsFalse( bundle.IsValid );
		StringAssert.Contains( bundle.Errors.Single(), "arm64-v8a" );
	}

	[TestMethod]
	public void RequiresSomeLibraries()
	{
		using var dir = new TempDir();
		dir.Dir( "lib/arm64-v8a" );
		dir.File( "lib/arm64-v8a/readme.txt" );

		var bundle = new RuntimeBundle( dir.Path, "sbox", false );
		Assert.IsFalse( bundle.IsValid );
		StringAssert.Contains( bundle.Errors.Single(), "No .so files" );
	}

	[TestMethod]
	public void RequiresTheLibraryTheActivityLoads()
	{
		using var dir = new TempDir();
		dir.File( "lib/arm64-v8a/libother.so" );

		var bundle = new RuntimeBundle( dir.Path, "sbox", false );
		Assert.IsFalse( bundle.IsValid );
		StringAssert.Contains( bundle.Errors.Single(), "libsbox.so" );

		Assert.IsTrue( new RuntimeBundle( dir.Path, "other", false ).IsValid, "library name follows the setting" );
	}

	[TestMethod]
	public void VrWarnsAboutMissingOpenXrLoaderButStillBuilds()
	{
		using var dir = new TempDir();
		dir.File( "lib/arm64-v8a/libsbox.so" );

		Assert.AreEqual( 0, new RuntimeBundle( dir.Path, "sbox", vr: false ).Warnings.Count );

		var vr = new RuntimeBundle( dir.Path, "sbox", vr: true );
		Assert.IsTrue( vr.IsValid );
		StringAssert.Contains( vr.Warnings.Single(), "libopenxr_loader.so" );

		dir.File( "lib/arm64-v8a/libopenxr_loader.so" );
		Assert.AreEqual( 0, new RuntimeBundle( dir.Path, "sbox", vr: true ).Warnings.Count );
	}

	[TestMethod]
	public void EntriesAreLibsStoredThenAssets()
	{
		using var dir = new TempDir();
		dir.File( "lib/arm64-v8a/libsbox.so" );
		dir.File( "lib/arm64-v8a/libb.so" );
		dir.File( "lib/arm64-v8a/notes.txt" ); // not a library
		dir.File( "assets/core/shaders/a.shader_c" );
		dir.File( "assets/cfg.txt" );
		dir.File( "other/ignored.txt" );

		var entries = new RuntimeBundle( dir.Path, "sbox", false ).Entries().ToList();

		CollectionAssert.AreEqual( new[]
		{
			"lib/arm64-v8a/libb.so",
			"lib/arm64-v8a/libsbox.so",
			"assets/cfg.txt",
			"assets/core/shaders/a.shader_c",
		}, entries.Select( e => e.Name ).ToArray() );

		Assert.IsTrue( entries.Take( 2 ).All( e => !e.Compress ) );
		Assert.IsTrue( entries.Skip( 2 ).All( e => e.Compress ) );
		Assert.IsTrue( entries.All( e => File.Exists( e.SourcePath ) ) );
	}

	[TestMethod]
	public void AssetsFolderIsOptional()
	{
		using var dir = new TempDir();
		dir.File( "lib/arm64-v8a/libsbox.so" );
		Assert.AreEqual( 1, new RuntimeBundle( dir.Path, "sbox", false ).Entries().Count() );
	}
}

[TestClass]
public class RuntimeManifestTests
{
	[TestMethod]
	public void GameEntriesLiveUnderTheGameRoot()
	{
		var fromDisk = AndroidRuntimeManifest.GameEntry( "models\\box.vmdl_c", "/abs/box.vmdl_c", null );
		Assert.AreEqual( "assets/sbox/game/models/box.vmdl_c", fromDisk.Name );
		Assert.AreEqual( "/abs/box.vmdl_c", fromDisk.SourcePath );

		var bytes = new byte[] { 1, 2 };
		var inMemory = AndroidRuntimeManifest.GameEntry( ".bin/game.dll", "/ignored", bytes );
		Assert.AreEqual( "assets/sbox/game/.bin/game.dll", inMemory.Name );
		Assert.AreSame( bytes, inMemory.Contents, "contents win over a path" );
		Assert.IsNull( inMemory.SourcePath );

		Assert.IsNull( AndroidRuntimeManifest.GameEntry( "empty", null, null ) );
	}

	[TestMethod]
	public void GameRootConstantsAgree()
	{
		StringAssert.StartsWith( AndroidRuntimeManifest.GameRoot, "assets/" );
		Assert.AreEqual( AndroidRuntimeManifest.GameRoot, "assets/" + new AndroidRuntimeManifest().GameRootPath );
	}

	[TestMethod]
	public void ManifestRoundTrips()
	{
		var manifest = new AndroidRuntimeManifest
		{
			Name = "Game",
			Ident = "org.game",
			PackageName = "com.org.game",
			BuildDate = new DateTime( 2026, 1, 2, 3, 4, 5, DateTimeKind.Utc ),
			IsVRProject = true,
		};

		var entry = manifest.ToEntry();
		Assert.AreEqual( "assets/sbox/android.json", entry.Name );

		var back = JsonSerializer.Deserialize<AndroidRuntimeManifest>( entry.Contents );
		Assert.AreEqual( "org.game", back.Ident );
		Assert.AreEqual( "com.org.game", back.PackageName );
		Assert.AreEqual( manifest.BuildDate, back.BuildDate );
		Assert.IsTrue( back.IsVRProject );
		Assert.AreEqual( "sbox/game/", back.GameRootPath );
	}
}

[TestClass]
public class AdbTests
{
	class Scripted : IToolRunner
	{
		public List<string[]> Calls { get; } = new();
		public ToolResult Next { get; set; } = new( 0, "" );

		public Task<ToolResult> Run( string executable, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment = null )
		{
			Calls.Add( arguments.ToArray() );
			return Task.FromResult( Next );
		}
	}

	const string DevicesOutput = @"* daemon not running; starting now at tcp:5037
* daemon started successfully
List of devices attached
2G0YC1ZF1T01234        device usb:1-1 product:eureka model:Quest_3 device:eureka transport_id:1
R5CT1234ABC            unauthorized usb:1-2 transport_id:2
192.168.1.20:5555      offline

";

	[TestMethod]
	public void ParsesDeviceListAndIgnoresNoise()
	{
		var devices = AdbClient.ParseDevices( DevicesOutput );

		Assert.AreEqual( 3, devices.Count );
		Assert.AreEqual( "2G0YC1ZF1T01234", devices[0].Serial );
		Assert.IsTrue( devices[0].IsReady );
		StringAssert.Contains( devices[0].Description, "model:Quest_3" );

		Assert.AreEqual( "unauthorized", devices[1].State );
		Assert.IsFalse( devices[1].IsReady );
		Assert.IsFalse( devices[2].IsReady );
		Assert.AreEqual( "", devices[2].Description );

		Assert.AreEqual( 0, AdbClient.ParseDevices( "List of devices attached\n\n" ).Count );
	}

	[TestMethod]
	public void NoAdbMeansAClearError()
	{
		var e = Assert.ThrowsException<FileNotFoundException>( () => new AdbClient( null, new Scripted() ) );
		StringAssert.Contains( e.Message, "platform-tools" );
	}

	[TestMethod]
	public async Task ListsDevices()
	{
		var runner = new Scripted { Next = new ToolResult( 0, DevicesOutput ) };
		var devices = await new AdbClient( "adb", runner ).Devices();

		Assert.AreEqual( 3, devices.Count );
		CollectionAssert.AreEqual( new[] { "devices", "-l" }, runner.Calls.Single() );

		runner.Next = new ToolResult( 1, "adb: error" );
		await Assert.ThrowsExceptionAsync<ApkBuildException>( () => new AdbClient( "adb", runner ).Devices() );
	}

	[TestMethod]
	public async Task InstallTargetsTheChosenDevice()
	{
		var runner = new Scripted { Next = new ToolResult( 0, "Performing Streamed Install\nSuccess" ) };
		var adb = new AdbClient( "adb", runner );

		await adb.Install( "/game.apk" );
		await adb.Install( "/game.apk", "SERIAL1" );

		CollectionAssert.AreEqual( new[] { "install", "-r", "/game.apk" }, runner.Calls[0] );
		CollectionAssert.AreEqual( new[] { "-s", "SERIAL1", "install", "-r", "/game.apk" }, runner.Calls[1] );
	}

	[TestMethod]
	public async Task InstallFailuresAreReported()
	{
		var adb = new AdbClient( "adb", new Scripted { Next = new ToolResult( 0, "Failure [INSTALL_FAILED_OLDER_SDK]" ) } );
		var e = await Assert.ThrowsExceptionAsync<ApkBuildException>( () => adb.Install( "/game.apk" ) );
		StringAssert.Contains( e.Message, "INSTALL_FAILED_OLDER_SDK" );

		var crashed = new AdbClient( "adb", new Scripted { Next = new ToolResult( 1, "Success? no" ) } );
		await Assert.ThrowsExceptionAsync<ApkBuildException>( () => crashed.Install( "/game.apk" ) );
	}

	[TestMethod]
	public async Task LaunchStartsTheNativeActivity()
	{
		var runner = new Scripted { Next = new ToolResult( 0, "Starting: Intent { cmp=com.test.game/android.app.NativeActivity }" ) };
		await new AdbClient( "adb", runner ).Launch( "com.test.game", "S" );

		CollectionAssert.AreEqual( new[] { "-s", "S", "shell", "am", "start", "-n", "com.test.game/android.app.NativeActivity" }, runner.Calls.Single() );
	}

	[TestMethod]
	public async Task LaunchNoticesErrorsThatExitZero()
	{
		// am start exits 0 when the component doesn't exist
		var adb = new AdbClient( "adb", new Scripted { Next = new ToolResult( 0, "Error type 3\nError: Activity class does not exist." ) } );
		await Assert.ThrowsExceptionAsync<ApkBuildException>( () => adb.Launch( "com.test.game" ) );

		var failed = new AdbClient( "adb", new Scripted { Next = new ToolResult( 1, "device offline" ) } );
		await Assert.ThrowsExceptionAsync<ApkBuildException>( () => failed.Launch( "com.test.game" ) );
	}
}

[TestClass]
public class KeystoreTests
{
	class CreatesFile : IToolRunner
	{
		public List<string[]> Calls { get; } = new();
		public int Exit { get; set; }

		public Task<ToolResult> Run( string executable, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment = null )
		{
			Calls.Add( arguments.ToArray() );
			if ( Exit == 0 ) File.WriteAllText( FakeToolRunner.ValueAfter( arguments.ToArray(), "-keystore" ), "ks" );
			return Task.FromResult( new ToolResult( Exit, "keytool said no" ) );
		}
	}

	[TestMethod]
	public void DefaultLocationIsTheSharedAndroidOne()
	{
		Assert.AreEqual( Path.Combine( "/home/me", ".android", "debug.keystore" ), DebugKeystore.DefaultPath( "/home/me" ) );
	}

	[TestMethod]
	public async Task ExistingKeystoreIsLeftAlone()
	{
		using var dir = new TempDir();
		var path = dir.File( "debug.keystore", "mine" );
		var runner = new CreatesFile();

		Assert.AreEqual( path, await DebugKeystore.EnsureExists( path, "keytool", runner ) );
		Assert.AreEqual( 0, runner.Calls.Count );
		Assert.AreEqual( "mine", File.ReadAllText( path ) );
	}

	[TestMethod]
	public async Task MissingKeystoreIsGeneratedWithTheStandardDebugCredentials()
	{
		using var dir = new TempDir();
		var path = Path.Combine( dir.Path, ".android", "debug.keystore" );
		var runner = new CreatesFile();

		await DebugKeystore.EnsureExists( path, "keytool", runner );

		Assert.IsTrue( File.Exists( path ) );
		var args = runner.Calls.Single();
		Assert.AreEqual( "androiddebugkey", FakeToolRunner.ValueAfter( args, "-alias" ) );
		Assert.AreEqual( "android", FakeToolRunner.ValueAfter( args, "-storepass" ) );
		Assert.AreEqual( "android", FakeToolRunner.ValueAfter( args, "-keypass" ) );
		Assert.AreEqual( "RSA", FakeToolRunner.ValueAfter( args, "-keyalg" ) );
	}

	[TestMethod]
	public async Task KeytoolFailureIsReported()
	{
		using var dir = new TempDir();
		var e = await Assert.ThrowsExceptionAsync<ApkBuildException>( () =>
			DebugKeystore.EnsureExists( Path.Combine( dir.Path, "k" ), "keytool", new CreatesFile { Exit = 1 } ) );
		StringAssert.Contains( e.Message, "keytool said no" );
	}
}

[TestClass]
public class ProcessRunnerTests
{
	static (string exe, string[] args) Shell( string script ) =>
		OperatingSystem.IsWindows() ? ("cmd.exe", new[] { "/c", script }) : ("/bin/sh", new[] { "-c", script });

	[TestMethod]
	public async Task CapturesOutputExitCodeAndEnvironment()
	{
		var (exe, args) = Shell( OperatingSystem.IsWindows() ? "echo %SBOX_TEST_VAR%& exit 3" : "echo $SBOX_TEST_VAR; echo err >&2; exit 3" );

		var result = await new ProcessToolRunner().Run( exe, args, new Dictionary<string, string> { ["SBOX_TEST_VAR"] = "hello" } );

		Assert.AreEqual( 3, result.ExitCode );
		Assert.IsFalse( result.Success );
		StringAssert.Contains( result.Output, "hello" );
		if ( !OperatingSystem.IsWindows() )
			StringAssert.Contains( result.Output, "err", "stderr is captured too" );
	}

	[TestMethod]
	public async Task SuccessIsExitCodeZero()
	{
		var (exe, args) = Shell( "exit 0" );
		Assert.IsTrue( (await new ProcessToolRunner().Run( exe, args )).Success );
	}

	[TestMethod]
	public async Task ArgumentsWithSpacesSurviveIntact()
	{
		if ( OperatingSystem.IsWindows() )
			return;

		var result = await new ProcessToolRunner().Run( "/bin/sh", new[] { "-c", "printf '%s|' \"$@\"", "sh", "a b", "c\"d" } );
		Assert.AreEqual( "a b|c\"d|", result.Output.Trim() );
	}
}
