/// <summary>
/// Test probe, built into a game by make-standalone.sh (EXTRA_CODE): once a second it logs the frame
/// count, the input the game sees, and where the first CubeController (the Sweeper sample's) is.
/// </summary>
public sealed class InputProbe : GameObjectSystem
{
	RealTimeSince last;
	int frames;

	public InputProbe( Scene scene ) : base( scene )
	{
		Listen( Stage.FinishUpdate, 0, Tick, "InputProbe" );
	}

	void Tick()
	{
		frames++;
		if ( last < 1 ) return;
		last = 0;

		var cube = Scene.GetAllComponents<CubeController>().FirstOrDefault();
		Log.Info( $"probe frames={frames} forward={Input.Down( "forward" )} move={Input.AnalogMove} pos={cube?.WorldPosition} yaw={cube?.WorldRotation.Angles().yaw}" );
		frames = 0;
	}
}
