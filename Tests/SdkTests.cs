using Editor.Android;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AndroidExportTests;

[TestClass]
public class SdkTests
{
	static void MakeSdk( TempDir dir, string[] buildTools, string[] platforms, bool exe = false, bool adb = true )
	{
		foreach ( var bt in buildTools )
		{
			var e = exe ? ".exe" : "";
			dir.File( $"build-tools/{bt}/aapt2{e}" );
			dir.File( $"build-tools/{bt}/zipalign{e}" );
			dir.File( $"build-tools/{bt}/apksigner{(exe ? ".bat" : "")}" );
		}

		foreach ( var p in platforms )
			dir.File( $"platforms/{p}/android.jar" );

		if ( adb )
			dir.File( $"platform-tools/adb{(exe ? ".exe" : "")}" );
	}

	[TestMethod]
	public void PicksNewestBuildToolsByVersionNotByName()
	{
		using var dir = new TempDir();
		// as strings "9.0.0" sorts after "34.0.0"
		MakeSdk( dir, new[] { "9.0.0", "34.0.0", "30.0.3" }, new[] { "android-34" } );
		dir.Dir( "build-tools/not-a-version" );

		var sdk = AndroidSdk.Resolve( dir.Path, 34 );

		Assert.AreEqual( "34.0.0", sdk.BuildToolsVersion );
		Assert.AreEqual( Path.Combine( dir.Path, "build-tools", "34.0.0", "aapt2" ), sdk.Aapt2 );
		Assert.AreEqual( Path.Combine( dir.Path, "build-tools", "34.0.0", "zipalign" ), sdk.ZipAlign );
		Assert.AreEqual( Path.Combine( dir.Path, "build-tools", "34.0.0", "apksigner" ), sdk.ApkSigner );
		Assert.AreEqual( Path.Combine( dir.Path, "platform-tools", "adb" ), sdk.Adb );
	}

	[TestMethod]
	public void PlatformIsLowestThatCoversTheTarget()
	{
		using var dir = new TempDir();
		MakeSdk( dir, new[] { "34.0.0" }, new[] { "android-31", "android-34", "android-35", "android-TiramisuPrivacySandbox" } );

		Assert.AreEqual( 31, AndroidSdk.Resolve( dir.Path, 30 ).PlatformLevel );
		Assert.AreEqual( 31, AndroidSdk.Resolve( dir.Path, 31 ).PlatformLevel );
		Assert.AreEqual( 34, AndroidSdk.Resolve( dir.Path, 32 ).PlatformLevel );
		Assert.AreEqual( 35, AndroidSdk.Resolve( dir.Path, 35 ).PlatformLevel );
		Assert.AreEqual( 35, AndroidSdk.Resolve( dir.Path, 99 ).PlatformLevel, "falls back to the newest installed" );
		Assert.IsTrue( AndroidSdk.Resolve( dir.Path, 34 ).AndroidJar.EndsWith( Path.Combine( "android-34", "android.jar" ) ) );
	}

	[TestMethod]
	public void WindowsUsesExeAndBatSuffixes()
	{
		using var dir = new TempDir();
		MakeSdk( dir, new[] { "34.0.0" }, new[] { "android-34" }, exe: true );

		var sdk = AndroidSdk.Resolve( dir.Path, 34, windows: true );

		Assert.IsTrue( sdk.Aapt2.EndsWith( "aapt2.exe" ) );
		Assert.IsTrue( sdk.ZipAlign.EndsWith( "zipalign.exe" ) );
		Assert.IsTrue( sdk.ApkSigner.EndsWith( "apksigner.bat" ) );
		Assert.IsTrue( sdk.Adb.EndsWith( "adb.exe" ) );
	}

	[TestMethod]
	public void AdbIsOptional()
	{
		using var dir = new TempDir();
		MakeSdk( dir, new[] { "34.0.0" }, new[] { "android-34" }, adb: false );
		Assert.IsNull( AndroidSdk.Resolve( dir.Path, 34 ).Adb );
	}

	[TestMethod]
	public void MissingPiecesAreNamed()
	{
		Assert.ThrowsException<FileNotFoundException>( () => AndroidSdk.Resolve( null, 34 ) );
		Assert.ThrowsException<FileNotFoundException>( () => AndroidSdk.Resolve( "/definitely/not/here", 34 ) );

		using var empty = new TempDir();
		var noBuildTools = Assert.ThrowsException<FileNotFoundException>( () => AndroidSdk.Resolve( empty.Path, 34 ) );
		StringAssert.Contains( noBuildTools.Message, "build-tools" );

		using var noPlatforms = new TempDir();
		MakeSdk( noPlatforms, new[] { "34.0.0" }, new string[0] );
		StringAssert.Contains( Assert.ThrowsException<FileNotFoundException>( () => AndroidSdk.Resolve( noPlatforms.Path, 34 ) ).Message, "platforms" );

		using var noTool = new TempDir();
		MakeSdk( noTool, new[] { "34.0.0" }, new[] { "android-34" } );
		File.Delete( Path.Combine( noTool.Path, "build-tools", "34.0.0", "zipalign" ) );
		StringAssert.Contains( Assert.ThrowsException<FileNotFoundException>( () => AndroidSdk.Resolve( noTool.Path, 34 ) ).Message, "zipalign" );

		using var noJar = new TempDir();
		MakeSdk( noJar, new[] { "34.0.0" }, new[] { "android-34" } );
		File.Delete( Path.Combine( noJar.Path, "platforms", "android-34", "android.jar" ) );
		StringAssert.Contains( Assert.ThrowsException<FileNotFoundException>( () => AndroidSdk.Resolve( noJar.Path, 34 ) ).Message, "android.jar" );
	}

	[TestMethod]
	public void CandidateRootsAreOrderedAndDeduplicated()
	{
		var env = new Dictionary<string, string> { ["ANDROID_HOME"] = "/a", ["ANDROID_SDK_ROOT"] = "/a", ["LOCALAPPDATA"] = "/local" };
		var roots = AndroidSdk.CandidateRoots( k => env.GetValueOrDefault( k ), "/home/me" ).ToArray();

		CollectionAssert.AreEqual( new[]
		{
			"/a",
			Path.Combine( "/local", "Android", "Sdk" ),
			Path.Combine( "/home/me", "Android", "Sdk" ),
			Path.Combine( "/home/me", "Library", "Android", "sdk" ),
		}, roots );
	}

	[TestMethod]
	public void FindRootSkipsFoldersThatArentSdks()
	{
		using var notSdk = new TempDir();
		using var sdk = new TempDir();
		MakeSdk( sdk, new[] { "34.0.0" }, new[] { "android-34" } );

		var env = new Dictionary<string, string> { ["ANDROID_HOME"] = notSdk.Path, ["ANDROID_SDK_ROOT"] = sdk.Path };

		Assert.AreEqual( sdk.Path, AndroidSdk.FindRoot( k => env.GetValueOrDefault( k ), "/nonexistent-home" ) );
		Assert.IsNull( AndroidSdk.FindRoot( k => null, "/nonexistent-home" ) );
	}
}
