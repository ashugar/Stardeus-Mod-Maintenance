using System;
using ImprovedShipPathing;

int checks = 0;
void Check(bool condition, string label) {
    checks++;
    if (!condition) throw new Exception(label);
}

InterceptInput Input(
        double px = 0, double py = 0, double pvx = 0, double pvy = 0,
        double pmax = 1, double pa = 0.1, double pd = 0.1,
        double tx = 10, double ty = 0, double tex = 100, double tey = 0,
        double tv = 0.2, double tw = 0.2, double ta = 0.05, double stand = 2,
        TargetTrajectoryKind kind = TargetTrajectoryKind.FiniteLine,
        double orbitDirection = 0, bool stop = true) =>
    new(px, py, pvx, pvy, pmax, pa, pd,
        tx, ty, tex, tey, tv, tw, ta, stand, kind, orbitDirection, stop);

Check(InterceptSolver.TrySolve(Input(), out var fleeing), "slower fleeing target can be intercepted");
Check(fleeing.TargetX > 10 && fleeing.TargetX < 100, "fleeing target is led along its course");
Check(fleeing.Ticks > 0, "intercept time is positive");

Check(InterceptSolver.TrySolve(Input(tx: 30, ty: 30, tex: 30, tey: -30,
    tv: 0.5, tw: 0.5), out var crossing), "crossing course can be intercepted");
Check(crossing.TargetY < 30, "crossing target is led rather than chased at current position");

Check(InterceptSolver.TrySolve(Input(pmax: 0.15, tx: 10, tex: 25,
    tv: 0.4, tw: 0.4), out var endpoint), "faster target can be caught after endpoint");
Check(Math.Abs(endpoint.TargetX - 25) < 0.0001, "unreachable moving target resolves to course endpoint");

Check(!InterceptSolver.TrySolve(Input(pmax: 0), out _), "no player speed fails open");
Check(!InterceptSolver.TrySolve(Input(tex: 10), out _), "target with no remaining course fails open");

Check(InterceptSolver.TrySolve(Input(stand: 0), out var noStand), "zero-standoff intercept solves");
Check(fleeing.Ticks < noStand.Ticks, "native standoff distance shortens rendezvous time");

Check(InterceptSolver.TrySolve(Input(pvx: -0.6, pmax: 1), out var turning),
    "opposite initial velocity still yields an intercept");
Check(turning.Ticks > fleeing.Ticks, "large turn includes Core stop-before-turn delay");

Check(InterceptSolver.TrySolve(Input(px: 0, py: -15, tx: 10, ty: 0,
    tex: 9.8, tey: 2, tv: 0.2, tw: 0.2,
    kind: TargetTrajectoryKind.CircularOrbit, orbitDirection: 1), out var orbit),
    "Core circular orbit can be intercepted");
Check(orbit.TargetY > 0 && Math.Abs(Math.Sqrt(orbit.TargetX * orbit.TargetX
    + orbit.TargetY * orbit.TargetY) - 10) < 0.0001,
    "orbit prediction advances around and remains on the circle");

Check(InterceptSolver.TrySolve(Input(stop: false), out var flyThrough),
    "fly-through intercept solves");
Check(flyThrough.Ticks < fleeing.Ticks,
    "fly-through contact is earlier than a stopped rendezvous");

double accelDistance = InterceptSolver.DistanceAtTime(10, 0, 1, 0.1);
Check(Math.Abs(accelDistance - 5) < 0.0001, "accelerating target distance");
Check(Math.Abs(InterceptSolver.TimeToTravel(5, 0, 1, 0.1) - 10) < 0.0001,
    "target travel inverse");
Check(Math.Abs(InterceptSolver.RestToRestDistance(20, 0, 1, 0.1, 0.1) - 10) < 0.0001,
    "triangular accelerate-brake envelope");
Check(Math.Abs(InterceptSolver.RestToRestDistance(30, 0, 1, 0.1, 0.1) - 20) < 0.0001,
    "trapezoidal accelerate-cruise-brake envelope");
Check(Math.Abs(InterceptSolver.AcceleratingDistance(15, 0, 1, 0.1) - 10) < 0.0001,
    "accelerate-then-cruise fly-through envelope");

Console.WriteLine($"Improved Ship Pathing solver checks passed: {checks}");
