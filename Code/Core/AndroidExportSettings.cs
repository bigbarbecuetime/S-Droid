using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Editor.Android;

/// <summary>
/// Meta headsets an APK can declare support for (<c>com.oculus.supportedDevices</c>).
/// Devices left out run in compatibility mode, where the runtime reports an older headset.
/// </summary>
[Flags]
public enum QuestDevice
{
	None = 0,
	Quest2 = 1,
	QuestPro = 2,
	Quest3 = 4,
	Quest3S = 8,
	All = Quest2 | QuestPro | Quest3 | Quest3S,
}

public enum HandTrackingMode
{
	Off,
	Optional,
	Required,
}

/// <summary>
/// Everything the Android exporter needs to know that isn't in the s&amp;box project itself.
/// </summary>
public class AndroidExportSettings
{
	/// <summary>Meta asks for min and target SDK 32 on Quest 2, Pro, 3 and 3S.</summary>
	public const int QuestSdkLevel = 32;

	public string PackageName { get; set; } = "com.example.mygame";
	public string AppLabel { get; set; } = "My Game";
	public int VersionCode { get; set; } = 1;
	public string VersionName { get; set; } = "1.0";
	public int MinSdk { get; set; } = 29;
	public int TargetSdk { get; set; } = 34;
	public bool Debuggable { get; set; }

	/// <summary>
	/// Name of the native library the activity loads, without <c>lib</c> prefix or <c>.so</c>.
	/// </summary>
	public string NativeLibraryName { get; set; } = "sbox";

	/// <summary>
	/// Folder holding the arm64 Android build of the s&amp;box runtime. Laid out as
	/// <c>lib/arm64-v8a/*.so</c>, with an optional <c>assets/</c> folder merged into the APK.
	/// </summary>
	public string RuntimeDirectory { get; set; }

	/// <summary>Android SDK folder. Null finds it through ANDROID_HOME and the usual install folders.</summary>
	public string SdkPath { get; set; }

	/// <summary>Output .apk path.</summary>
	public string OutputPath { get; set; }

	public bool EnableMicrophone { get; set; }
	public List<string> ExtraPermissions { get; set; } = new();

	// VR (Meta Quest)
	public bool EnableVR { get; set; }
	public QuestDevice SupportedDevices { get; set; } = QuestDevice.Quest3 | QuestDevice.Quest3S;
	public HandTrackingMode HandTracking { get; set; } = HandTrackingMode.Off;
	public bool Passthrough { get; set; }

	/// <summary>
	/// Optional <c>metavr:uses-metavr-sdk</c> levels. Left out of the manifest when null, see Meta's
	/// "Meta VR OS SDK Versioning" page for valid values.
	/// </summary>
	public int? MetaVrMinSdk { get; set; }
	public int? MetaVrTargetSdk { get; set; }

	// Signing. Null KeystorePath means the standard ~/.android/debug.keystore.
	public string KeystorePath { get; set; }
	public string KeystoreAlias { get; set; } = "androiddebugkey";
	public string KeystorePassword { get; set; } = "android";
	public string KeyPassword { get; set; } = "android";

	/// <summary>
	/// Starting settings for a project: a package name derived from its org and ident, which are
	/// lower-cased with anything Android won't accept replaced.
	/// </summary>
	public static AndroidExportSettings ForProject( string org, string ident, string title )
	{
		return new AndroidExportSettings
		{
			PackageName = $"com.{PackageSegment( org )}.{PackageSegment( ident )}",
			AppLabel = string.IsNullOrWhiteSpace( title ) ? ident : title,
			NativeLibraryName = "sbox",
		};
	}

	/// <summary>One dot-separated part of a package name: [a-z][a-z0-9_]*, and never a Java keyword.</summary>
	public static string PackageSegment( string value )
	{
		var chars = (value ?? "").ToLowerInvariant().Select( c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' ? c : '_' ).ToArray();
		var segment = new string( chars ).Trim( '_' );

		if ( segment.Length == 0 || !char.IsAsciiLetter( segment[0] ) )
			segment = "app" + segment;

		return JavaKeywords.Contains( segment ) ? segment + "_" : segment;
	}

	/// <summary>Switch SDK levels to what Quest requires.</summary>
	public void ApplyQuestPreset()
	{
		EnableVR = true;
		MinSdk = QuestSdkLevel;
		TargetSdk = QuestSdkLevel;
	}

	static readonly Regex PackageRegex = new( @"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z][A-Za-z0-9_]*)+$", RegexOptions.Compiled );
	static readonly Regex LibraryRegex = new( @"^[A-Za-z0-9_]+$", RegexOptions.Compiled );
	static readonly Regex PermissionRegex = new( @"^[A-Za-z][A-Za-z0-9_.]*$", RegexOptions.Compiled );

	// Reserved words can't be a segment of an Android package name
	static readonly HashSet<string> JavaKeywords = new( StringComparer.Ordinal )
	{
		"abstract","assert","boolean","break","byte","case","catch","char","class","const","continue","default","do",
		"double","else","enum","extends","final","finally","float","for","goto","if","implements","import","instanceof",
		"int","interface","long","native","new","package","private","protected","public","return","short","static",
		"strictfp","super","switch","synchronized","this","throw","throws","transient","try","void","volatile","while",
		"true","false","null"
	};

	public static bool IsValidPackageName( string name )
	{
		if ( string.IsNullOrEmpty( name ) || !PackageRegex.IsMatch( name ) )
			return false;

		foreach ( var segment in name.Split( '.' ) )
		{
			if ( JavaKeywords.Contains( segment ) )
				return false;
		}

		return true;
	}

	public static bool IsValidPermission( string name ) => !string.IsNullOrEmpty( name ) && PermissionRegex.IsMatch( name );

	/// <summary>
	/// Check the settings on their own, without touching the disk. Returns one message per problem.
	/// </summary>
	/// <param name="includePaths">False to skip the runtime and output paths, which the manifest doesn't need.</param>
	public List<string> Validate( bool includePaths = true )
	{
		var errors = new List<string>();

		if ( !IsValidPackageName( PackageName ) )
			errors.Add( $"'{PackageName}' isn't a valid Android package name (e.g. com.studio.game, no Java keywords)" );

		if ( string.IsNullOrWhiteSpace( AppLabel ) )
			errors.Add( "App label is empty" );

		if ( VersionCode < 1 )
			errors.Add( "Version code must be at least 1" );

		if ( string.IsNullOrWhiteSpace( VersionName ) )
			errors.Add( "Version name is empty" );

		if ( MinSdk < 1 )
			errors.Add( "Min SDK must be at least 1" );

		if ( TargetSdk < MinSdk )
			errors.Add( $"Target SDK ({TargetSdk}) is lower than min SDK ({MinSdk})" );

		if ( string.IsNullOrEmpty( NativeLibraryName ) || !LibraryRegex.IsMatch( NativeLibraryName ) )
			errors.Add( $"'{NativeLibraryName}' isn't a valid native library name" );

		if ( includePaths && string.IsNullOrWhiteSpace( RuntimeDirectory ) )
			errors.Add( "No Android runtime directory set" );

		if ( includePaths && string.IsNullOrWhiteSpace( OutputPath ) )
			errors.Add( "No output path set" );

		foreach ( var permission in ExtraPermissions )
		{
			if ( !IsValidPermission( permission ) )
				errors.Add( $"'{permission}' isn't a valid permission name" );
		}

		if ( EnableVR )
		{
			if ( SupportedDevices == QuestDevice.None )
				errors.Add( "VR is enabled but no Quest device is selected" );

			if ( TargetSdk < QuestSdkLevel )
				errors.Add( $"Quest needs target SDK {QuestSdkLevel} or higher (got {TargetSdk})" );

			if ( MetaVrMinSdk.HasValue != MetaVrTargetSdk.HasValue )
				errors.Add( "Set both Meta VR min and target SDK, or neither" );
			else if ( MetaVrMinSdk > MetaVrTargetSdk )
				errors.Add( "Meta VR min SDK is higher than its target SDK" );
		}
		else if ( HandTracking != HandTrackingMode.Off || Passthrough )
		{
			errors.Add( "Hand tracking and passthrough are only available when VR is enabled" );
		}

		return errors;
	}
}
