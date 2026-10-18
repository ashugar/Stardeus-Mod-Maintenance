using System;
using System.Collections.Generic;
using MissileOverhaul;

int checks = 0;
void Check(bool condition, string label) {
    checks++;
    if (!condition) throw new Exception(label);
}
int[] Assign(int[,] priorities, int[] flights = null, int[] existing = null) {
    var distances = new float[priorities.GetLength(0), priorities.GetLength(1)];
    for (int w=0; w<priorities.GetLength(0); w++)
        for (int t=0; t<priorities.GetLength(1); t++) distances[w,t] = t + 1;
    if (existing == null) {
        existing = new int[priorities.GetLength(0)];
        Array.Fill(existing, -1);
    }
    return DefensePolicy.Assign(priorities, distances, existing,
        flights ?? new int[priorities.GetLength(1)]);
}
var two = Assign(new int[,] { {3,3}, {3,3} });
Check(two[0] != two[1], "two buildings cover two threats");
var constrained = Assign(new int[,] { {3,3}, {3,0} });
Check(constrained[0] == 1 && constrained[1] == 0, "restricted launcher keeps its sole eligible threat");
var disabled = Assign(new int[,] { {0,3}, {0,0} });
Check(disabled[0] == 1 && disabled[1] == -1, "disabled target types and no eligible target");
var flying = Assign(new int[,] {{3,3}}, new int[] {1,0});
Check(flying[0] == 1, "missile in flight gives uncovered threat priority");
Check(Assign(new int[5,0]).Length == 5, "empty threat list");
Check(Assign(new int[0,5]).Length == 0, "empty launcher list");
string[] weaponIds = { "InterceptorTurret", "RocketPod" };
DefenseTargetKind[] kinds = { DefenseTargetKind.IncomingMissile,
    DefenseTargetKind.Hullpiercer, DefenseTargetKind.Meteoroid, DefenseTargetKind.Other,
    DefenseTargetKind.SuspectedStorageCapsule };
for (int w=0; w<weaponIds.Length; w++) for (int t=0; t<kinds.Length; t++) {
    bool expected = w == 0 ? t < 3 : t >= 3;
    Check(DefensePolicy.RoleAllows(weaponIds[w], kinds[t]) == expected,
        "explicit division of weapon responsibility");
}
Check(!DefensePolicy.Supports("LaserTurret"), "other weapons outside overhaul");
Check(!DefensePolicy.RoleAllows("LaserTurret", DefenseTargetKind.Other), "unknown weapon has no managed role");
Check(DefensePolicy.Manages("RocketPod", true, true), "automatic player Rocket Pod is managed");
Check(!DefensePolicy.Manages("RocketPod", true, false), "manual player Rocket Pod remains native");
Check(DefensePolicy.Manages("RocketPod", false, false), "allied or hostile Rocket Pod is managed by ship AI");
Check(DefensePolicy.Manages("InterceptorTurret", false, false), "allied or hostile Interceptor is managed by ship AI");
Check(!DefensePolicy.Manages("LaserTurret", false, false), "other non-player weapons remain native");
int[] RoleAssignments(string[] weapons, DefenseTargetKind[] targetKinds, int[] flights = null) {
    var p = new int[weapons.Length,targetKinds.Length];
    for (int w=0; w<weapons.Length; w++) for (int t=0; t<targetKinds.Length; t++)
        p[w,t] = DefensePolicy.RoleAllows(weapons[w],targetKinds[t]) ? 3 : 0;
    return Assign(p,flights);
}
var mixedKinds = new[] { DefenseTargetKind.Meteoroid, DefenseTargetKind.Hullpiercer,
    DefenseTargetKind.Other, DefenseTargetKind.Other };
var mixed = RoleAssignments(new[] {"InterceptorTurret","InterceptorTurret","RocketPod","RocketPod"},mixedKinds);
Check(mixed[0] != mixed[1] && mixed[0] < 2 && mixed[1] < 2,
    "interceptors cover distinct dedicated threats");
Check(mixed[2] != mixed[3] && mixed[2] >= 2 && mixed[3] >= 2,
    "rocket pods cover distinct remaining threats");
var onlyRockets = RoleAssignments(new[] {"RocketPod","RocketPod"},
    new[] {DefenseTargetKind.Meteoroid, DefenseTargetKind.Hullpiercer});
Check(onlyRockets[0] == -1 && onlyRockets[1] == -1,
    "rocket pods do not replace missing interceptor defenses");
var onlyInterceptors = RoleAssignments(new[] {"InterceptorTurret","InterceptorTurret"},
    new[] {DefenseTargetKind.Other});
Check(onlyInterceptors[0] == -1 && onlyInterceptors[1] == -1,
    "interceptors do not take rocket targets when rocket pods are absent");
var mixedFlights = RoleAssignments(new[] {"InterceptorTurret","RocketPod"},mixedKinds,new[]{1,0,1,0});
Check(mixedFlights[0] == 1 && mixedFlights[1] == 3,
    "both roles prefer their own uncovered threats");
var stable = Assign(new int[,] { {3,3},{3,3} },existing:new[]{1,0});
Check(stable[0] == 1 && stable[1] == 0,"valid balanced assignments remain stable");
var rng = new Random(482);
int MaximumCoverage(int[,] p, int row, int used) {
    if (row == p.GetLength(0)) return 0;
    int best = MaximumCoverage(p, row+1, used);
    for (int t=0; t<p.GetLength(1); t++)
        if (p[row,t]>0 && (used & (1<<t)) == 0)
            best = Math.Max(best, 1 + MaximumCoverage(p, row+1, used | (1<<t)));
    return best;
}
for (int sample=0; sample<500; sample++) {
    int wc=rng.Next(1,7), tc=rng.Next(1,7);
    var p = new int[wc,tc];
    for (int w=0; w<wc; w++) for (int t=0; t<tc; t++) p[w,t]=rng.Next(0,5);
    var a=Assign(p);
    var unique=new HashSet<int>();
    for (int w=0; w<wc; w++) {
        Check(a[w] < 0 || p[w,a[w]] > 0, "random assignment is eligible");
        if (a[w]>=0) unique.Add(a[w]);
    }
    Check(unique.Count == MaximumCoverage(p,0,0), "coverage matches independent exhaustive search");
}
for (int wc=1; wc<=24; wc++) for (int tc=1; tc<=12; tc++) {
    var p=new int[wc,tc];
    for (int w=0; w<wc; w++) for (int t=0; t<tc; t++) p[w,t]=3;
    var a=Assign(p); var load=new int[tc]; foreach (int t in a) load[t]++;
    int min=int.MaxValue,max=0; foreach (int n in load) { min=Math.Min(min,n); max=Math.Max(max,n); }
    Check(max-min<=1,"uniform surplus load remains balanced");
}
Check(Math.Abs(DefensePolicy.HitChance(.90f,.05f)-.855f)<.00001,"interceptor meteoroid hit chance");
Check(Math.Abs(DefensePolicy.HitChance(.90f,.25f)-.675f)<.00001,"interceptor Hullpiercer hit chance");
Check(Math.Abs(DefensePolicy.HitChance(.85f,.10f)-.765f)<.00001,"rocket asteroid hit chance remains native");
Check(Math.Abs(DefensePolicy.HitChance(.85f,.50f)-.425f)<.00001,"unaligned breach capsule retains native evasion");
Check(DefensePolicy.HitChance(.85f,.50f,true)==1f,"tractor-aligned trajectory is a guaranteed hit");
Check(DefensePolicy.HitChance(1,2)==.01f,"Core minimum evade adjustment");
Check(!DefensePolicy.SuspectedStorageRaidActive(false,5),"five storage capsules remain ordinary cargo");
Check(DefensePolicy.SuspectedStorageRaidActive(false,6),"six storage capsules activate suspected raid defense");
Check(DefensePolicy.SuspectedStorageRaidActive(true,1),"suspected raid remains active for its last flying capsule");
Check(!DefensePolicy.SuspectedStorageRaidActive(true,0),"suspected raid resets after all flying capsules clear");
foreach (var test in new (double x,double y,double vx,double vy,double speed)[] {
    (100,0,0,0,50),(100,0,-15,0,50),(100,0,15,0,50),
    (0,100,20,0,50),(100,100,-20,-5,50),(100,0,-50,0,50) }) {
    Check(DefensePolicy.Intercept(test.x,test.y,test.vx,test.vy,test.speed,out double time),"intercept exists");
    double x=test.x+test.vx*time,y=test.y+test.vy*time;
    Check(Math.Abs(Math.Sqrt(x*x+y*y)-test.speed*time)<.00001,"flight distance equals intercept duration");
}
Check(!DefensePolicy.Intercept(100,0,60,0,50,out _),"receding threat faster than missile");
Check(!DefensePolicy.Intercept(100,0,0,0,0,out _),"zero projectile speed rejected");
Check(!DefensePolicy.Intercept(double.NaN,0,0,0,50,out _),"invalid position rejected");
Check(DefensePolicy.Intercept(100,50,-10,5,50,out double normal),"normal speed flight");
Check(DefensePolicy.Intercept(100,50,-30,15,150,out double fast),"three times scaled simulation speed flight");
Check(Math.Abs(fast*3-normal)<.00001,"simulation speed correction preserves flight duration in ticks");
var original=new object(); var changed=new object();
var lease=new ShotLease<object>(original);
object current=changed;
Check(ReferenceEquals(lease.Target,original)&&!ReferenceEquals(lease.Target,current),"retargeting preserves launched missile identity");
Check(lease.Complete()&&!lease.Complete(),"save completion and normal completion cannot resolve twice");
Console.WriteLine($"PASS: {checks} checks. Roles, coverage, evasion, interception and flight identity.");
