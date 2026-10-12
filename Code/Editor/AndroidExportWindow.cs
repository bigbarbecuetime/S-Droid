using Editor.Android;

namespace Editor;

/// <summary>
/// Settings and export buttons for the Android / Quest exporter.
/// </summary>
public class AndroidExportWindow : BaseWindow
{
	readonly Project project;
	readonly AndroidExportSettings settings;
	Label status;
	bool busy;

	[Menu( "Editor", "Settings/Android Export...", "android" )]
	public static void Open()
	{
		var project = Project.Current;
		if ( project is null )
		{
			EditorUtility.DisplayDialog( "Android Export", "Open a project first." );
			return;
		}

		new AndroidExportWindow( project ).Show();
	}

	public AndroidExportWindow( Project project )
	{
		this.project = project;
		settings = AndroidExporter.LoadSettings( project );

		WindowTitle = "S&Droid: Export for Android";
		SetWindowIcon( "android" );
		Size = new Vector2( 640, 760 );
		MinimumSize = new Vector2( 480, 480 );

		Build();
	}

	void Build()
	{
		Layout = Layout.Column();
		Layout.Margin = 8;
		Layout.Spacing = 4;

		var scroll = Layout.Add( new ScrollArea( this ), 1 );
		scroll.Canvas = new Widget( scroll );
		scroll.Canvas.Layout = Layout.Column();
		scroll.Canvas.Layout.Spacing = 4;

		var so = settings.GetSerialized();
		foreach ( var property in so )
		{
			var row = scroll.Canvas.Layout.AddRow();
			row.Spacing = 8;
			row.Add( new Label( property.DisplayName ) { MinimumWidth = 180 } );
			row.Add( ControlWidget.Create( property ), 1 );
		}

		status = Layout.Add( new Label( "" ) );

		var buttons = Layout.AddRow();
		buttons.Spacing = 4;

		buttons.Add( new Button( "Quest 3 Preset" )
		{
			ToolTip = "Turns on VR and sets min and target SDK to 32, as Meta requires for Quest",
			Clicked = () =>
			{
				settings.ApplyQuestPreset();
				Layout.Clear( true );
				Build();
			}
		} );

		buttons.AddStretchCell();
		buttons.Add( new Button( "Export" ) { Clicked = () => _ = Run( deploy: false ) } );
		buttons.Add( new Button.Primary( "Export and Run on Device" ) { Clicked = () => _ = Run( deploy: true ) } );
	}

	async Task Run( bool deploy )
	{
		if ( busy )
			return;

		busy = true;
		AndroidExporter.SaveSettings( settings );

		void Report( string message )
		{
			Log.Info( $"[Android] {message}" );
			status.Text = message;
		}

		try
		{
			await AndroidExporter.Export( project, settings, Report );

			if ( deploy )
				await AndroidExporter.Deploy( settings, Report );

			Report( deploy ? "Done, running on device" : $"Done: {settings.OutputPath}" );
		}
		catch ( Exception e )
		{
			Log.Error( e.Message );
			status.Text = "Failed, see console";
			EditorUtility.DisplayDialog( "Android Export Failed", e.Message );
		}
		finally
		{
			busy = false;
		}
	}
}
