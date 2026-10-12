using Editor.Android;
using System;
using System.Linq;
using System.Xml.Linq;

namespace AndroidExportTests;

[TestClass]
public class ManifestTests
{
	static readonly XNamespace A = "http://schemas.android.com/apk/res/android";
	static readonly XNamespace M = "http://schemas.meta.com/metavr-sdk";

	static AndroidExportSettings Phone() => new() { PackageName = "com.test.game", AppLabel = "Test", VersionCode = 7, VersionName = "1.2" };

	static AndroidExportSettings Quest()
	{
		var s = Phone();
		s.ApplyQuestPreset();
		return s;
	}

	static XElement Parse( AndroidExportSettings s ) => XDocument.Parse( AndroidManifestBuilder.Build( s ) ).Root;

	static string[] FeatureNames( XElement m ) => m.Elements( "uses-feature" ).Select( e => (string)e.Attribute( A + "name" ) ).ToArray();
	static XElement Feature( XElement m, string name ) => m.Elements( "uses-feature" ).Single( e => (string)e.Attribute( A + "name" ) == name );
	static string[] Permissions( XElement m ) => m.Elements( "uses-permission" ).Select( e => (string)e.Attribute( A + "name" ) ).ToArray();
	static string[] Categories( XElement m ) => m.Descendants( "category" ).Select( e => (string)e.Attribute( A + "name" ) ).ToArray();

	[TestMethod]
	public void PhoneBuildIsAPlainNativeActivity()
	{
		var m = Parse( Phone() );

		Assert.AreEqual( "com.test.game", (string)m.Attribute( "package" ) );
		Assert.AreEqual( "7", (string)m.Attribute( A + "versionCode" ) );
		Assert.AreEqual( "1.2", (string)m.Attribute( A + "versionName" ) );

		var sdk = m.Element( "uses-sdk" );
		Assert.AreEqual( "29", (string)sdk.Attribute( A + "minSdkVersion" ) );
		Assert.AreEqual( "34", (string)sdk.Attribute( A + "targetSdkVersion" ) );

		var application = m.Element( "application" );
		Assert.AreEqual( "false", (string)application.Attribute( A + "hasCode" ), "no dex, the library is the app" );
		Assert.AreEqual( "false", (string)application.Attribute( A + "debuggable" ) );

		var activity = application.Element( "activity" );
		Assert.AreEqual( "android.app.NativeActivity", (string)activity.Attribute( A + "name" ) );
		Assert.AreEqual( "sensorLandscape", (string)activity.Attribute( A + "screenOrientation" ) );
		Assert.AreEqual( "sbox", (string)activity.Element( "meta-data" ).Attribute( A + "value" ) );
		Assert.AreEqual( "android.app.lib_name", (string)activity.Element( "meta-data" ).Attribute( A + "name" ) );

		CollectionAssert.AreEqual( new[] { "android.intent.category.LAUNCHER" }, Categories( m ) );
		CollectionAssert.AreEqual( new[] { "android.hardware.vulkan.version" }, FeatureNames( m ) );
		CollectionAssert.AreEqual( new[] { "android.permission.INTERNET" }, Permissions( m ) );
		Assert.IsNull( m.Attribute( XNamespace.Xmlns + "metavr" ), "Meta namespace is only for VR builds" );
		Assert.IsNull( application.Element( "meta-data" ), "no supportedDevices on a phone build" );
	}

	[TestMethod]
	public void QuestBuildFollowsMetasManifestRequirements()
	{
		var m = Parse( Quest() );

		var headtracking = Feature( m, "android.hardware.vr.headtracking" );
		Assert.AreEqual( "true", (string)headtracking.Attribute( A + "required" ) );
		Assert.AreEqual( "1", (string)headtracking.Attribute( A + "version" ) );

		var sdk = m.Element( "uses-sdk" );
		Assert.AreEqual( "32", (string)sdk.Attribute( A + "minSdkVersion" ) );
		Assert.AreEqual( "32", (string)sdk.Attribute( A + "targetSdkVersion" ) );

		var activity = m.Element( "application" ).Element( "activity" );
		Assert.AreEqual( "landscape", (string)activity.Attribute( A + "screenOrientation" ) );
		Assert.AreEqual( "false", (string)activity.Attribute( A + "resizeableActivity" ) );
		Assert.AreEqual( "singleTask", (string)activity.Attribute( A + "launchMode" ) );
		Assert.AreEqual( "@android:style/Theme.Black.NoTitleBar.Fullscreen", (string)activity.Attribute( A + "theme" ) );
		Assert.IsNull( activity.Attribute( A + "noHistory" ) );

		CollectionAssert.AreEquivalent(
			new[] { "android.intent.category.LAUNCHER", "com.oculus.intent.category.VR", "org.khronos.openxr.intent.category.IMMERSIVE_HMD" },
			Categories( m ) );

		var devices = m.Element( "application" ).Element( "meta-data" );
		Assert.AreEqual( "com.oculus.supportedDevices", (string)devices.Attribute( A + "name" ) );
		Assert.AreEqual( "quest3|quest3s", (string)devices.Attribute( A + "value" ) );
	}

	[TestMethod]
	public void SupportedDeviceList()
	{
		Assert.AreEqual( "quest2|questpro|quest3|quest3s", AndroidManifestBuilder.QuestDeviceList( QuestDevice.All ) );
		Assert.AreEqual( "quest3", AndroidManifestBuilder.QuestDeviceList( QuestDevice.Quest3 ) );
		Assert.AreEqual( "questpro|quest3s", AndroidManifestBuilder.QuestDeviceList( QuestDevice.Quest3S | QuestDevice.QuestPro ) );
		Assert.AreEqual( "", AndroidManifestBuilder.QuestDeviceList( QuestDevice.None ) );
	}

	[TestMethod]
	public void MetaVrSdkElementOnlyWhenConfigured()
	{
		var s = Quest();
		Assert.IsNull( Parse( s ).Element( M + "uses-metavr-sdk" ) );

		s.MetaVrMinSdk = 201;
		s.MetaVrTargetSdk = 207;
		var e = Parse( s ).Element( M + "uses-metavr-sdk" );
		Assert.AreEqual( "201", (string)e.Attribute( M + "minSdkVersion" ) );
		Assert.AreEqual( "207", (string)e.Attribute( M + "targetSdkVersion" ) );

		// A phone build ignores them
		var phone = Phone();
		phone.MetaVrMinSdk = 201;
		phone.MetaVrTargetSdk = 207;
		Assert.IsNull( Parse( phone ).Element( M + "uses-metavr-sdk" ) );
	}

	[TestMethod]
	public void HandTrackingModes()
	{
		var s = Quest();

		var off = Parse( s );
		CollectionAssert.DoesNotContain( FeatureNames( off ), "oculus.software.handtracking" );
		CollectionAssert.DoesNotContain( Permissions( off ), "com.oculus.permission.HAND_TRACKING" );

		s.HandTracking = HandTrackingMode.Optional;
		var optional = Parse( s );
		Assert.AreEqual( "false", (string)Feature( optional, "oculus.software.handtracking" ).Attribute( A + "required" ) );
		CollectionAssert.Contains( Permissions( optional ), "com.oculus.permission.HAND_TRACKING" );

		s.HandTracking = HandTrackingMode.Required;
		Assert.AreEqual( "true", (string)Feature( Parse( s ), "oculus.software.handtracking" ).Attribute( A + "required" ) );
	}

	[TestMethod]
	public void PassthroughIsAnOptionalFeature()
	{
		var s = Quest();
		CollectionAssert.DoesNotContain( FeatureNames( Parse( s ) ), "com.oculus.feature.PASSTHROUGH" );

		s.Passthrough = true;
		Assert.AreEqual( "false", (string)Feature( Parse( s ), "com.oculus.feature.PASSTHROUGH" ).Attribute( A + "required" ) );
	}

	[TestMethod]
	public void PermissionsAreDeduplicated()
	{
		var s = Phone();
		s.EnableMicrophone = true;
		s.ExtraPermissions.AddRange( new[] { "android.permission.RECORD_AUDIO", "android.permission.INTERNET", "android.permission.VIBRATE" } );

		CollectionAssert.AreEqual(
			new[] { "android.permission.INTERNET", "android.permission.RECORD_AUDIO", "android.permission.VIBRATE" },
			Permissions( Parse( s ) ) );
	}

	[TestMethod]
	public void DebuggableFlag()
	{
		var s = Phone();
		s.Debuggable = true;
		Assert.AreEqual( "true", (string)Parse( s ).Element( "application" ).Attribute( A + "debuggable" ) );
	}

	[TestMethod]
	public void LabelIsEscaped()
	{
		var s = Phone();
		s.AppLabel = "Tom & \"Jerry\" <3";
		Assert.AreEqual( s.AppLabel, (string)Parse( s ).Element( "application" ).Attribute( A + "label" ) );
	}

	[TestMethod]
	public void InvalidSettingsAreRejectedBeforeWritingAnything()
	{
		var s = Phone();
		s.PackageName = "bad";
		var e = Assert.ThrowsException<ArgumentException>( () => AndroidManifestBuilder.Build( s ) );
		StringAssert.Contains( e.Message, "bad" );
	}

	[TestMethod]
	public void MissingPathsDontBlockTheManifest()
	{
		// RuntimeDirectory and OutputPath aren't the manifest's business
		Assert.IsNotNull( AndroidManifestBuilder.Build( Phone() ) );
	}

	[TestMethod]
	public void LaunchComponentMatchesTheActivity()
	{
		Assert.AreEqual( "com.test.game/android.app.NativeActivity", AndroidManifestBuilder.LaunchComponent( "com.test.game" ) );
	}
}
