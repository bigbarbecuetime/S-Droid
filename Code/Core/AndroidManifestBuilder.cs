using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace Editor.Android;

/// <summary>
/// Builds AndroidManifest.xml. Quest entries follow Meta's native manifest docs
/// (developers.meta.com/horizon/documentation/native/android/mobile-native-manifest) and the
/// OpenXR page (.../mobile-openxr) for the Khronos immersive HMD category.
/// </summary>
public static class AndroidManifestBuilder
{
	static readonly XNamespace Android = "http://schemas.android.com/apk/res/android";
	static readonly XNamespace MetaVr = "http://schemas.meta.com/metavr-sdk";

	/// <summary>Android's Vulkan 1.0.3 feature version, which is what mobile GPUs advertise as 1.0.</summary>
	const string Vulkan10 = "0x400003";

	const string ActivityClass = "android.app.NativeActivity";

	/// <summary>The component to hand to <c>am start -n</c> for this app.</summary>
	public static string LaunchComponent( string packageName ) => $"{packageName}/{ActivityClass}";

	public static string QuestDeviceList( QuestDevice devices )
	{
		var names = new List<string>();
		if ( devices.HasFlag( QuestDevice.Quest2 ) ) names.Add( "quest2" );
		if ( devices.HasFlag( QuestDevice.QuestPro ) ) names.Add( "questpro" );
		if ( devices.HasFlag( QuestDevice.Quest3 ) ) names.Add( "quest3" );
		if ( devices.HasFlag( QuestDevice.Quest3S ) ) names.Add( "quest3s" );
		return string.Join( "|", names );
	}

	public static string Build( AndroidExportSettings s )
	{
		var errors = s.Validate( includePaths: false );
		if ( errors.Count > 0 )
			throw new ArgumentException( "Invalid Android settings: " + string.Join( "; ", errors ) );

		var manifest = new XElement( "manifest",
			new XAttribute( XNamespace.Xmlns + "android", Android.NamespaceName ),
			new XAttribute( "package", s.PackageName ),
			new XAttribute( Android + "versionCode", s.VersionCode ),
			new XAttribute( Android + "versionName", s.VersionName ),
			new XAttribute( Android + "installLocation", "auto" ) );

		if ( s.EnableVR )
			manifest.Add( new XAttribute( XNamespace.Xmlns + "metavr", MetaVr.NamespaceName ) );

		manifest.Add( new XElement( "uses-sdk",
			new XAttribute( Android + "minSdkVersion", s.MinSdk ),
			new XAttribute( Android + "targetSdkVersion", s.TargetSdk ) ) );

		if ( s.EnableVR && s.MetaVrMinSdk.HasValue )
		{
			manifest.Add( new XElement( MetaVr + "uses-metavr-sdk",
				new XAttribute( MetaVr + "minSdkVersion", s.MetaVrMinSdk.Value ),
				new XAttribute( MetaVr + "targetSdkVersion", s.MetaVrTargetSdk.Value ) ) );
		}

		// Permissions, deduplicated and in a stable order
		var permissions = new List<string> { "android.permission.INTERNET" };
		if ( s.EnableMicrophone ) permissions.Add( "android.permission.RECORD_AUDIO" );
		if ( s.EnableVR && s.HandTracking != HandTrackingMode.Off ) permissions.Add( "com.oculus.permission.HAND_TRACKING" );
		permissions.AddRange( s.ExtraPermissions );

		foreach ( var permission in permissions.Distinct( StringComparer.Ordinal ) )
			manifest.Add( new XElement( "uses-permission", new XAttribute( Android + "name", permission ) ) );

		manifest.Add( Feature( "android.hardware.vulkan.version", true, version: Vulkan10 ) );

		if ( s.EnableVR )
		{
			manifest.Add( Feature( "android.hardware.vr.headtracking", true, version: "1" ) );

			if ( s.HandTracking != HandTrackingMode.Off )
				manifest.Add( Feature( "oculus.software.handtracking", s.HandTracking == HandTrackingMode.Required ) );

			if ( s.Passthrough )
				manifest.Add( Feature( "com.oculus.feature.PASSTHROUGH", false ) );
		}

		manifest.Add( BuildApplication( s ) );

		var document = new XDocument( new XDeclaration( "1.0", "utf-8", null ), manifest );
		return document.Declaration + Environment.NewLine + document.Root;
	}

	static XElement Feature( string name, bool required, string version = null )
	{
		var e = new XElement( "uses-feature",
			new XAttribute( Android + "name", name ),
			new XAttribute( Android + "required", required ? "true" : "false" ) );

		if ( version is not null )
			e.Add( new XAttribute( Android + "version", version ) );

		return e;
	}

	static XElement BuildApplication( AndroidExportSettings s )
	{
		var application = new XElement( "application",
			new XAttribute( Android + "label", s.AppLabel ),
			// Pure native app: no dex, the activity is the platform's NativeActivity
			new XAttribute( Android + "hasCode", "false" ),
			new XAttribute( Android + "allowBackup", "false" ),
			// Map libs straight out of the (page aligned) APK instead of duplicating them on disk
			new XAttribute( Android + "extractNativeLibs", "false" ),
			new XAttribute( Android + "debuggable", s.Debuggable ? "true" : "false" ) );

		if ( s.EnableVR )
		{
			application.Add( new XElement( "meta-data",
				new XAttribute( Android + "name", "com.oculus.supportedDevices" ),
				new XAttribute( Android + "value", QuestDeviceList( s.SupportedDevices ) ) ) );
		}

		var intentFilter = new XElement( "intent-filter",
			new XElement( "action", new XAttribute( Android + "name", "android.intent.action.MAIN" ) ),
			new XElement( "category", new XAttribute( Android + "name", "android.intent.category.LAUNCHER" ) ) );

		if ( s.EnableVR )
		{
			intentFilter.Add( new XElement( "category", new XAttribute( Android + "name", "com.oculus.intent.category.VR" ) ) );
			intentFilter.Add( new XElement( "category", new XAttribute( Android + "name", "org.khronos.openxr.intent.category.IMMERSIVE_HMD" ) ) );
		}

		var activity = new XElement( "activity",
			new XAttribute( Android + "name", ActivityClass ),
			new XAttribute( Android + "exported", "true" ),
			new XAttribute( Android + "theme", "@android:style/Theme.Black.NoTitleBar.Fullscreen" ),
			new XAttribute( Android + "screenOrientation", s.EnableVR ? "landscape" : "sensorLandscape" ),
			new XAttribute( Android + "configChanges", "density|keyboard|keyboardHidden|navigation|orientation|screenLayout|screenSize|uiMode" ),
			new XAttribute( Android + "launchMode", "singleTask" ),
			new XAttribute( Android + "resizeableActivity", "false" ),
			new XElement( "meta-data",
				new XAttribute( Android + "name", "android.app.lib_name" ),
				new XAttribute( Android + "value", s.NativeLibraryName ) ),
			intentFilter );

		application.Add( activity );
		return application;
	}
}
