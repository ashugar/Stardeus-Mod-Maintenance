# Validation

## Automated

- Build `mod.csproj` in Release.
- Run `Tests/InterceptSolver.Tests.csproj`.
- Confirm the installed `Game.dll` contains exact `ShipSys.OnTickProgress(float)` plus the required `ShipSys` fields and `NPCShip.NearbyPosFrom(Vector2, out bool, float)`.
- Confirm `ModInfo.json`, project version, assembly/file/informational versions, and packaged DLL agree.
- Restart Stardeus and inspect a fresh `Player.log` for one clean 0.1.1 initialization with no Harmony or solver exception.

## Gameplay acceptance still required

1. With a moving neutral ship, choose **Travel Here** from behind it. Confirm the player ship visibly turns toward a point ahead of the target and reaches native standoff range without manual retargeting.
2. Repeat with a lateral/crossing course.
3. Repeat against a ship using Core's circular orbit behavior. Confirm the plotted destination leads around the orbit rather than following the current short waypoint.
4. Attack a fleeing eligible hostile ship. Confirm the plotted destination extends through the meeting point, interception occurs, and Core starts combat only after native range/eligibility checks.
5. Test a target faster than the player but approaching a finite local endpoint. Confirm the player aims at the endpoint rather than trailing indefinitely.
6. Change or reverse the target's course during pursuit. Confirm the player recalculates without oscillation or an incorrect arrival fanfare.
7. Cancel flight, retarget a non-ship object, enter hyperspace, and save/load during pursuit. Confirm native behavior remains intact.
8. Test inadequate engine power and loss of bridge operation. Confirm the mod does not bypass Core's readiness or engine behavior.

Do not describe 0.1.1 as gameplay-validated until these checks pass in the restarted game.
