using Editor.Android;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AndroidExportTests;

[TestClass]
public class PackagerTests
{
	static AndroidSdk Sdk( TempDir dir )
	{
		var tools = dir.Dir( "build-tools/34.0.0" );
		foreach ( var tool in new[] { "aapt2", "zipalign", "apksigner" } ) dir.File( $"build-tools/34.0.0/{tool}" );
		dir.File( "platforms/android-34/android.jar" );
		return AndroidSdk.Resolve( dir.Path, 34 );
	}

	static AndroidExportSettings Settings( TempDir dir ) => new()
	{
		PackageName = "com.test.game",
		VersionCode = 3,
		VersionName = "0.3",
		MinSdk = 32,
		TargetSdk = 32,
		KeystoreAlias = "key",
		KeystorePassword = "storepw",
		KeyPassword = "keypw",
		OutputPath = Path.Combine( dir.Path, "out", "game.apk" ),
	};

	[TestMethod]
	public async Task RunsTheToolsInOrderWithTheRightArguments()
	{
		using var dir = new TempDir();
		var sdk = Sdk( dir );
		var settings = Settings( dir );
		var runner = new FakeToolRunner();
		var log = new List<string>();

		await new ApkPackager( sdk, runner, log.Add ).Package( settings, "<manifest/>", new[] { new ApkEntry( "assets/a.txt", Contents: new byte[] { 1 } ) }, "/my.keystore", Path.Combine( dir.Path, "work" ) );

		CollectionAssert.AreEqual( new[] { "aapt2", "zipalign", "apksigner", "apksigner" }, runner.Calls.Select( c => Path.GetFileName( c.Tool ) ).ToArray() );

		var link = runner.Calls[0].Args;
		Assert.AreEqual( "link", link[0] );
		Assert.AreEqual( sdk.AndroidJar, FakeToolRunner.ValueAfter( link, "-I" ) );
		Assert.AreEqual( "32", FakeToolRunner.ValueAfter( link, "--min-sdk-version" ) );
		Assert.AreEqual( "32", FakeToolRunner.ValueAfter( link, "--target-sdk-version" ) );
		Assert.AreEqual( "3", FakeToolRunner.ValueAfter( link, "--version-code" ) );
		Assert.AreEqual( "0.3", FakeToolRunner.ValueAfter( link, "--version-name" ) );
		Assert.AreEqual( "<manifest/>", File.ReadAllText( FakeToolRunner.ValueAfter( link, "--manifest" ) ) );

		var align = runner.Calls[1].Args;
		CollectionAssert.IsSubsetOf( new[] { "-p", "4" }, align, "page aligned for extractNativeLibs=false" );

		var sign = runner.Calls[2].Args;
		Assert.AreEqual( "sign", sign[0] );
		Assert.AreEqual( "/my.keystore", FakeToolRunner.ValueAfter( sign, "--ks" ) );
		Assert.AreEqual( "key", FakeToolRunner.ValueAfter( sign, "--ks-key-alias" ) );
		Assert.AreEqual( settings.OutputPath, FakeToolRunner.ValueAfter( sign, "--out" ) );

		var verify = runner.Calls[3].Args;
		CollectionAssert.AreEqual( new[] { "verify", settings.OutputPath }, verify );

		Assert.IsTrue( File.Exists( settings.OutputPath ) );
		StringAssert.Contains( log.Last(), settings.OutputPath );
	}

	[TestMethod]
	public async Task PasswordsGoThroughTheEnvironmentNotTheCommandLine()
	{
		using var dir = new TempDir();
		var runner = new FakeToolRunner();

		await new ApkPackager( Sdk( dir ), runner ).Package( Settings( dir ), "<manifest/>", new ApkEntry[0], "/ks", Path.Combine( dir.Path, "work" ) );

		var sign = runner.Calls.First( c => c.Args[0] == "sign" );
		Assert.IsFalse( sign.Args.Any( a => a.Contains( "storepw" ) || a.Contains( "keypw" ) ), "passwords must not be visible in argv" );

		var storePw = FakeToolRunner.ValueAfter( sign.Args, "--ks-pass" );
		var keyPw = FakeToolRunner.ValueAfter( sign.Args, "--key-pass" );
		Assert.IsTrue( storePw.StartsWith( "env:" ) && keyPw.StartsWith( "env:" ) );
		Assert.AreEqual( "storepw", sign.Env[storePw["env:".Length..]] );
		Assert.AreEqual( "keypw", sign.Env[keyPw["env:".Length..]] );
	}

	[TestMethod]
	public async Task OutputContainsLinkedFilesPlusEntriesWithRightCompression()
	{
		using var dir = new TempDir();
		var settings = Settings( dir );
		var text = Encoding.UTF8.GetBytes( string.Concat( Enumerable.Repeat( "compress me ", 500 ) ) );
		var lib = dir.File( "libsbox.so", string.Concat( Enumerable.Repeat( "ELF", 500 ) ) );

		var entries = new[]
		{
			new ApkEntry( "lib/arm64-v8a/libsbox.so", SourcePath: lib, Compress: false ),
			// a caller asking for compression on a .so is overruled, zipalign can't align deflated data
			new ApkEntry( "lib/arm64-v8a/libother.so", SourcePath: lib, Compress: true ),
			new ApkEntry( "assets/sbox/game/data.txt", Contents: text ),
			new ApkEntry( "assets/sbox/game/raw.bin", Contents: text, Compress: false ),
		};

		await new ApkPackager( Sdk( dir ), new FakeToolRunner() ).Package( settings, "<manifest/>", entries, "/ks", Path.Combine( dir.Path, "work" ) );

		var methods = ZipInspector.Methods( settings.OutputPath );
		Assert.AreEqual( 0, methods["lib/arm64-v8a/libsbox.so"] );
		Assert.AreEqual( 0, methods["lib/arm64-v8a/libother.so"] );
		Assert.AreEqual( 8, methods["assets/sbox/game/data.txt"] );
		Assert.AreEqual( 0, methods["assets/sbox/game/raw.bin"] );
		Assert.AreEqual( 0, methods["resources.arsc"], "Android 11+ rejects a compressed resource table" );
		Assert.IsTrue( methods.ContainsKey( "AndroidManifest.xml" ) );

		using var zip = ZipFile.OpenRead( settings.OutputPath );
		using var reader = new StreamReader( zip.GetEntry( "assets/sbox/game/data.txt" ).Open() );
		Assert.AreEqual( Encoding.UTF8.GetString( text ), reader.ReadToEnd() );
		using var libReader = new StreamReader( zip.GetEntry( "lib/arm64-v8a/libsbox.so" ).Open() );
		Assert.AreEqual( File.ReadAllText( lib ), libReader.ReadToEnd() );
	}

	[TestMethod]
	public async Task ReplacesAnExistingOutputApk()
	{
		using var dir = new TempDir();
		var settings = Settings( dir );
		Directory.CreateDirectory( Path.GetDirectoryName( settings.OutputPath ) );
		File.WriteAllText( settings.OutputPath, "stale" );

		await new ApkPackager( Sdk( dir ), new FakeToolRunner() ).Package( settings, "<manifest/>", new ApkEntry[0], "/ks", Path.Combine( dir.Path, "work" ) );

		using var zip = ZipFile.OpenRead( settings.OutputPath );
		Assert.IsNotNull( zip.GetEntry( "AndroidManifest.xml" ) );
	}

	[TestMethod]
	[DataRow( "aapt2", 1 )]
	[DataRow( "zipalign", 2 )]
	[DataRow( "apksigner", 3 )]
	public async Task ToolFailureStopsTheBuildAndShowsItsOutput( string failing, int callsMade )
	{
		using var dir = new TempDir();
		var runner = new FakeToolRunner();
		runner.Failures[failing] = new ToolResult( 2, "boom: details" );

		var e = await Assert.ThrowsExceptionAsync<ApkBuildException>( () =>
			new ApkPackager( Sdk( dir ), runner ).Package( Settings( dir ), "<manifest/>", new ApkEntry[0], "/ks", Path.Combine( dir.Path, "work" ) ) );

		StringAssert.Contains( e.Message, "boom: details" );
		StringAssert.Contains( e.Message, "exit code 2" );
		Assert.AreEqual( callsMade, runner.Calls.Count, "nothing runs after the failing step" );
	}

	[TestMethod]
	public async Task FailedVerificationFailsTheBuild()
	{
		using var dir = new TempDir();
		var runner = new VerifyFailsRunner();

		var e = await Assert.ThrowsExceptionAsync<ApkBuildException>( () =>
			new ApkPackager( Sdk( dir ), runner ).Package( Settings( dir ), "<manifest/>", new ApkEntry[0], "/ks", Path.Combine( dir.Path, "work" ) ) );

		StringAssert.Contains( e.Message, "verify" );
	}

	class VerifyFailsRunner : IToolRunner
	{
		readonly FakeToolRunner inner = new();

		public async Task<ToolResult> Run( string executable, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment = null )
		{
			var result = await inner.Run( executable, arguments, environment );
			return arguments[0] == "verify" ? new ToolResult( 1, "DOES NOT VERIFY" ) : result;
		}
	}

	[TestMethod]
	public void DuplicateEntriesAreRejected()
	{
		using var dir = new TempDir();
		var source = Path.Combine( dir.Path, "in.apk" );
		using ( var z = ZipFile.Open( source, ZipArchiveMode.Create ) ) z.CreateEntry( "AndroidManifest.xml" );

		var dest = Path.Combine( dir.Path, "out.apk" );

		// duplicates a file aapt2 already produced
		Assert.ThrowsException<ApkBuildException>( () => ApkPackager.AddEntries( source, dest, new[] { new ApkEntry( "AndroidManifest.xml", Contents: new byte[0] ) } ) );

		// duplicates another added entry, even spelled with a backslash
		Assert.ThrowsException<ApkBuildException>( () => ApkPackager.AddEntries( source, dest, new[]
		{
			new ApkEntry( "assets/a", Contents: new byte[0] ),
			new ApkEntry( "assets\\a", Contents: new byte[0] ),
		} ) );
	}

	[TestMethod]
	[DataRow( "../escape.txt" )]
	[DataRow( "assets/../../escape.txt" )]
	[DataRow( "" )]
	[DataRow( "/" )]
	public void BadEntryNamesAreRejected( string name )
	{
		using var dir = new TempDir();
		var source = Path.Combine( dir.Path, "in.apk" );
		using ( var z = ZipFile.Open( source, ZipArchiveMode.Create ) ) z.CreateEntry( "AndroidManifest.xml" );

		Assert.ThrowsException<ApkBuildException>( () => ApkPackager.AddEntries( source, Path.Combine( dir.Path, "out.apk" ), new[] { new ApkEntry( name, Contents: new byte[0] ) } ) );
	}

	[TestMethod]
	public void EntryNamesAreNormalised()
	{
		using var dir = new TempDir();
		var source = Path.Combine( dir.Path, "in.apk" );
		using ( var z = ZipFile.Open( source, ZipArchiveMode.Create ) ) z.CreateEntry( "AndroidManifest.xml" );
		var dest = Path.Combine( dir.Path, "out.apk" );

		ApkPackager.AddEntries( source, dest, new[] { new ApkEntry( "/assets\\sbox\\file.txt", Contents: new byte[] { 1 } ) } );

		using var zip = ZipFile.OpenRead( dest );
		Assert.IsNotNull( zip.GetEntry( "assets/sbox/file.txt" ) );
	}
}
