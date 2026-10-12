using Editor.Android;
using System.Linq;

namespace AndroidExportTests;

[TestClass]
public class SettingsTests
{
	static AndroidExportSettings Valid() => new() { RuntimeDirectory = "/rt", OutputPath = "/out.apk" };

	[TestMethod]
	[DataRow( "com.example.game", true )]
	[DataRow( "a.b", true )]
	[DataRow( "com.My_Studio.Game2", true )]
	[DataRow( "game", false )]            // needs at least two segments
	[DataRow( "", false )]
	[DataRow( null, false )]
	[DataRow( "com..game", false )]
	[DataRow( "com.1game", false )]       // segment can't start with a digit
	[DataRow( "com.my-game", false )]
	[DataRow( "com.example.new", false )] // Java keyword
	[DataRow( "com.example.", false )]
	public void PackageNames( string name, bool valid )
	{
		Assert.AreEqual( valid, AndroidExportSettings.IsValidPackageName( name ) );
	}

	[TestMethod]
	[DataRow( "Facepunch", "facepunch" )]
	[DataRow( "my game!", "my_game" )]
	[DataRow( "123abc", "app123abc" )]
	[DataRow( "", "app" )]
	[DataRow( null, "app" )]
	[DataRow( "class", "class_" )]
	[DataRow( "___", "app" )]
	public void PackageSegmentIsAlwaysValid( string input, string expected )
	{
		var segment = AndroidExportSettings.PackageSegment( input );
		Assert.AreEqual( expected, segment );
		Assert.IsTrue( AndroidExportSettings.IsValidPackageName( $"com.{segment}.{segment}" ) );
	}

	[TestMethod]
	public void ForProjectDerivesPackageAndLabel()
	{
		var s = AndroidExportSettings.ForProject( "Local", "My Cool-Game", "My Cool Game" );
		Assert.AreEqual( "com.local.my_cool_game", s.PackageName );
		Assert.AreEqual( "My Cool Game", s.AppLabel );

		// no title falls back to the ident
		Assert.AreEqual( "ident", AndroidExportSettings.ForProject( "o", "ident", " " ).AppLabel );
	}

	[TestMethod]
	public void DefaultsWithPathsAreValid()
	{
		Assert.AreEqual( 0, Valid().Validate().Count );
	}

	[TestMethod]
	public void PathsAreOnlyRequiredWhenAsked()
	{
		var s = new AndroidExportSettings();
		Assert.AreEqual( 2, s.Validate().Count );
		Assert.AreEqual( 0, s.Validate( includePaths: false ).Count );
	}

	[TestMethod]
	public void ReportsEachProblem()
	{
		var s = Valid();
		s.PackageName = "nope";
		s.AppLabel = " ";
		s.VersionCode = 0;
		s.VersionName = "";
		s.NativeLibraryName = "libs/x";
		s.ExtraPermissions.Add( "bad permission" );

		Assert.AreEqual( 6, s.Validate().Count );
	}

	[TestMethod]
	public void TargetSdkCannotBeBelowMin()
	{
		var s = Valid();
		s.MinSdk = 33;
		s.TargetSdk = 30;
		Assert.AreEqual( 1, s.Validate().Count );

		s.TargetSdk = 33; // equal is fine
		Assert.AreEqual( 0, s.Validate().Count );
	}

	[TestMethod]
	public void QuestNeedsSdk32()
	{
		var s = Valid();
		s.EnableVR = true;
		s.TargetSdk = 31;
		Assert.IsTrue( s.Validate().Any( e => e.Contains( "target SDK 32" ) ) );

		s.TargetSdk = 32;
		Assert.AreEqual( 0, s.Validate().Count );
	}

	[TestMethod]
	public void QuestPresetFixesSdkLevels()
	{
		var s = Valid();
		s.TargetSdk = 29;
		s.ApplyQuestPreset();

		Assert.IsTrue( s.EnableVR );
		Assert.AreEqual( 32, s.MinSdk );
		Assert.AreEqual( 32, s.TargetSdk );
		Assert.AreEqual( 0, s.Validate().Count );
	}

	[TestMethod]
	public void VrNeedsAtLeastOneDevice()
	{
		var s = Valid();
		s.ApplyQuestPreset();
		s.SupportedDevices = QuestDevice.None;
		Assert.AreEqual( 1, s.Validate().Count );
	}

	[TestMethod]
	public void MetaVrSdkRange()
	{
		var s = Valid();
		s.ApplyQuestPreset();

		s.MetaVrMinSdk = 201;
		Assert.AreEqual( 1, s.Validate().Count, "only one end set" );

		s.MetaVrTargetSdk = 200;
		Assert.AreEqual( 1, s.Validate().Count, "min above target" );

		s.MetaVrTargetSdk = 207;
		Assert.AreEqual( 0, s.Validate().Count );

		s.MetaVrMinSdk = null;
		Assert.AreEqual( 1, s.Validate().Count, "only the target set" );
	}

	[TestMethod]
	public void HandTrackingAndPassthroughNeedVr()
	{
		var s = Valid();
		s.HandTracking = HandTrackingMode.Optional;
		Assert.AreEqual( 1, s.Validate().Count );

		s.HandTracking = HandTrackingMode.Off;
		s.Passthrough = true;
		Assert.AreEqual( 1, s.Validate().Count );

		s.ApplyQuestPreset();
		Assert.AreEqual( 0, s.Validate().Count );
	}
}
