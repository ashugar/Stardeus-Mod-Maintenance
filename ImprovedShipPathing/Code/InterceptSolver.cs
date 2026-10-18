using System;

namespace ImprovedShipPathing {
    public enum TargetTrajectoryKind {
        FiniteLine,
        CircularOrbit
    }

    /// <summary>
    /// Engine-independent interception math. Coordinates and speeds are expressed in
    /// sector-map units and game ticks, so the policy can be tested without Unity.
    /// </summary>
    public static class InterceptSolver {
        private const double Epsilon = 0.000001;
        private const double CosThirtyDegrees = 0.8660254037844386;
        private const double MaxPredictionTicks = 2000000d;

        public static bool TrySolve(InterceptInput input, out InterceptSolution solution) {
            solution = default;
            if (!input.IsFinite || input.PlayerMaxSpeed <= Epsilon
                    || input.PlayerAcceleration <= Epsilon
                    || input.PlayerDeceleration <= Epsilon) {
                return false;
            }

            double pathX = input.TargetEndX - input.TargetX;
            double pathY = input.TargetEndY - input.TargetY;
            double pathLength = Length(pathX, pathY);
            if (input.TrajectoryKind == TargetTrajectoryKind.FiniteLine
                    && pathLength <= Epsilon) return false;
            if (input.TrajectoryKind == TargetTrajectoryKind.CircularOrbit
                    && (Length(input.TargetX, input.TargetY) <= Epsilon
                        || Math.Abs(input.OrbitDirection) <= Epsilon)) return false;

            double pathDirX = pathLength > Epsilon ? pathX / pathLength : 0d;
            double pathDirY = pathLength > Epsilon ? pathY / pathLength : 0d;
            double targetArrival = input.TrajectoryKind == TargetTrajectoryKind.FiniteLine
                ? TimeToTravel(pathLength, input.TargetSpeed,
                    input.TargetWantedSpeed, input.TargetAcceleration)
                : double.PositiveInfinity;

            double previousTime = 0d;
            double time = 1d;
            double denseHorizon = input.TrajectoryKind == TargetTrajectoryKind.CircularOrbit
                ? 4096d
                : Math.Min(8192d, Math.Max(
                    IsFinite(targetArrival) ? targetArrival + 64d : 4096d, 256d));

            // While the target is moving, keep samples close enough to catch a
            // crossing course. Once it reaches its endpoint the condition becomes
            // monotonic, so exponentially wider samples are safe and inexpensive.
            while (time <= denseHorizon + Epsilon) {
                if (CanArriveBy(input, pathDirX, pathDirY, pathLength, time,
                        out double targetX, out double targetY)) {
                    return Refine(input, pathDirX, pathDirY, pathLength,
                        previousTime, time, out solution);
                }
                previousTime = time;
                time += time < 128d ? 1d : 4d;
            }

            // Beyond the dense search, widen samples. A finite target eventually
            // stops; an orbit remains bounded, while player reach keeps growing.
            time = Math.Max(previousTime + 1d, denseHorizon + 1d);
            while (time <= MaxPredictionTicks) {
                if (CanArriveBy(input, pathDirX, pathDirY, pathLength, time,
                        out _, out _)) {
                    return Refine(input, pathDirX, pathDirY, pathLength,
                        previousTime, time, out solution);
                }
                previousTime = time;
                time = Math.Ceiling(time * 1.5d + 1d);
            }
            return false;
        }

        private static bool Refine(InterceptInput input,
                double pathDirX, double pathDirY, double pathLength,
                double low, double high, out InterceptSolution solution) {
            for (int i = 0; i < 40; i++) {
                double mid = (low + high) * 0.5d;
                if (CanArriveBy(input, pathDirX, pathDirY, pathLength, mid,
                        out _, out _)) high = mid;
                else low = mid;
            }
            TargetPosition(input, pathDirX, pathDirY, pathLength, high,
                out double targetX, out double targetY);
            solution = new InterceptSolution(high, targetX, targetY);
            return solution.IsFinite;
        }

        private static bool CanArriveBy(InterceptInput input,
                double pathDirX, double pathDirY, double pathLength, double time,
                out double targetX, out double targetY) {
            TargetPosition(input, pathDirX, pathDirY, pathLength, time,
                out targetX, out targetY);
            double toTargetX = targetX - input.PlayerX;
            double toTargetY = targetY - input.PlayerY;
            double centerDistance = Length(toTargetX, toTargetY);
            double requiredDistance = Math.Max(0d, centerDistance - input.StandoffDistance);
            if (requiredDistance <= Epsilon) return true;

            double dirX = toTargetX / centerDistance;
            double dirY = toTargetY / centerDistance;
            double currentSpeed = Length(input.PlayerVelocityX, input.PlayerVelocityY);
            double alongSpeed = Math.Max(0d,
                input.PlayerVelocityX * dirX + input.PlayerVelocityY * dirY);
            double turnDelay = 0d;
            if (currentSpeed > Epsilon && alongSpeed / currentSpeed < CosThirtyDegrees) {
                // Core stops before a turn greater than 30 degrees. Treat that stop
                // as elapsed time rather than claiming its sideways motion as reach.
                turnDelay = currentSpeed / input.PlayerDeceleration;
                alongSpeed = 0d;
            }
            double travelTime = time - turnDelay;
            if (travelTime <= 0d) return false;
            double reachable = input.StopAtIntercept
                ? RestToRestDistance(travelTime, alongSpeed,
                    input.PlayerMaxSpeed, input.PlayerAcceleration,
                    input.PlayerDeceleration)
                : AcceleratingDistance(travelTime, alongSpeed,
                    input.PlayerMaxSpeed, input.PlayerAcceleration);
            return reachable + 0.00001d >= requiredDistance;
        }

        private static void TargetPosition(InterceptInput input,
                double pathDirX, double pathDirY, double pathLength, double time,
                out double x, out double y) {
            double traveled = DistanceAtTime(time, input.TargetSpeed,
                input.TargetWantedSpeed, input.TargetAcceleration);
            if (input.TrajectoryKind == TargetTrajectoryKind.CircularOrbit) {
                double radius = Length(input.TargetX, input.TargetY);
                double startAngle = Math.Atan2(input.TargetY, input.TargetX);
                double angle = startAngle + Math.Sign(input.OrbitDirection) * traveled / radius;
                x = radius * Math.Cos(angle);
                y = radius * Math.Sin(angle);
                return;
            }
            traveled = Math.Min(pathLength, traveled);
            x = input.TargetX + pathDirX * traveled;
            y = input.TargetY + pathDirY * traveled;
        }

        internal static double DistanceAtTime(double time,
                double currentSpeed, double wantedSpeed, double acceleration) {
            if (time <= 0d) return 0d;
            currentSpeed = Math.Max(0d, currentSpeed);
            wantedSpeed = Math.Max(0d, wantedSpeed);
            acceleration = Math.Max(0d, acceleration);
            if (acceleration <= Epsilon || Math.Abs(wantedSpeed - currentSpeed) <= Epsilon)
                return currentSpeed * time;

            if (wantedSpeed > currentSpeed) {
                double changingTime = (wantedSpeed - currentSpeed) / acceleration;
                if (time <= changingTime)
                    return currentSpeed * time + 0.5d * acceleration * time * time;
                double changingDistance = currentSpeed * changingTime
                    + 0.5d * acceleration * changingTime * changingTime;
                return changingDistance + wantedSpeed * (time - changingTime);
            } else {
                double changingTime = (currentSpeed - wantedSpeed) / acceleration;
                if (time <= changingTime)
                    return currentSpeed * time - 0.5d * acceleration * time * time;
                double changingDistance = currentSpeed * changingTime
                    - 0.5d * acceleration * changingTime * changingTime;
                return changingDistance + wantedSpeed * (time - changingTime);
            }
        }

        internal static double TimeToTravel(double distance,
                double currentSpeed, double wantedSpeed, double acceleration) {
            if (distance <= 0d) return 0d;
            currentSpeed = Math.Max(0d, currentSpeed);
            wantedSpeed = Math.Max(0d, wantedSpeed);
            acceleration = Math.Max(0d, acceleration);
            if (acceleration <= Epsilon || Math.Abs(wantedSpeed - currentSpeed) <= Epsilon)
                return currentSpeed > Epsilon ? distance / currentSpeed : double.PositiveInfinity;

            double changingTime = Math.Abs(wantedSpeed - currentSpeed) / acceleration;
            double changingDistance = DistanceAtTime(changingTime,
                currentSpeed, wantedSpeed, acceleration);
            if (distance <= changingDistance + Epsilon) {
                if (wantedSpeed > currentSpeed) {
                    return (-currentSpeed + Math.Sqrt(currentSpeed * currentSpeed
                        + 2d * acceleration * distance)) / acceleration;
                }
                double discriminant = Math.Max(0d,
                    currentSpeed * currentSpeed - 2d * acceleration * distance);
                return (currentSpeed - Math.Sqrt(discriminant)) / acceleration;
            }
            return wantedSpeed > Epsilon
                ? changingTime + (distance - changingDistance) / wantedSpeed
                : double.PositiveInfinity;
        }

        internal static double RestToRestDistance(double time, double currentSpeed,
                double maxSpeed, double acceleration, double deceleration) {
            if (time <= 0d || maxSpeed <= Epsilon
                    || acceleration <= Epsilon || deceleration <= Epsilon) return 0d;
            currentSpeed = Math.Max(0d, currentSpeed);
            maxSpeed = Math.Max(maxSpeed, currentSpeed);
            double minimumStopTime = currentSpeed / deceleration;
            if (time + Epsilon < minimumStopTime) return 0d;

            double accelerateTime = (deceleration * time - currentSpeed)
                / (acceleration + deceleration);
            accelerateTime = Math.Max(0d, accelerateTime);
            double peakSpeed = currentSpeed + acceleration * accelerateTime;
            if (peakSpeed <= maxSpeed + Epsilon) {
                double decelerateTime = time - accelerateTime;
                return currentSpeed * accelerateTime
                    + 0.5d * acceleration * accelerateTime * accelerateTime
                    + 0.5d * peakSpeed * decelerateTime;
            }

            accelerateTime = Math.Max(0d, (maxSpeed - currentSpeed) / acceleration);
            double decelTime = maxSpeed / deceleration;
            double cruiseTime = Math.Max(0d, time - accelerateTime - decelTime);
            return (currentSpeed + maxSpeed) * 0.5d * accelerateTime
                + maxSpeed * cruiseTime + 0.5d * maxSpeed * decelTime;
        }

        internal static double AcceleratingDistance(double time, double currentSpeed,
                double maxSpeed, double acceleration) {
            if (time <= 0d || maxSpeed <= Epsilon || acceleration <= Epsilon) return 0d;
            currentSpeed = Math.Max(0d, Math.Min(currentSpeed, maxSpeed));
            double accelerateTime = Math.Min(time, (maxSpeed - currentSpeed) / acceleration);
            double accelerationDistance = currentSpeed * accelerateTime
                + 0.5d * acceleration * accelerateTime * accelerateTime;
            return accelerationDistance + maxSpeed * (time - accelerateTime);
        }

        private static double Length(double x, double y) => Math.Sqrt(x * x + y * y);
        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }

    public readonly struct InterceptInput {
        public readonly double PlayerX, PlayerY;
        public readonly double PlayerVelocityX, PlayerVelocityY;
        public readonly double PlayerMaxSpeed, PlayerAcceleration, PlayerDeceleration;
        public readonly double TargetX, TargetY, TargetEndX, TargetEndY;
        public readonly double TargetSpeed, TargetWantedSpeed, TargetAcceleration;
        public readonly double StandoffDistance;
        public readonly TargetTrajectoryKind TrajectoryKind;
        public readonly double OrbitDirection;
        public readonly bool StopAtIntercept;

        public InterceptInput(double playerX, double playerY,
                double playerVelocityX, double playerVelocityY,
                double playerMaxSpeed, double playerAcceleration, double playerDeceleration,
                double targetX, double targetY, double targetEndX, double targetEndY,
                double targetSpeed, double targetWantedSpeed, double targetAcceleration,
                double standoffDistance,
                TargetTrajectoryKind trajectoryKind = TargetTrajectoryKind.FiniteLine,
                double orbitDirection = 0d,
                bool stopAtIntercept = true) {
            PlayerX = playerX; PlayerY = playerY;
            PlayerVelocityX = playerVelocityX; PlayerVelocityY = playerVelocityY;
            PlayerMaxSpeed = playerMaxSpeed; PlayerAcceleration = playerAcceleration;
            PlayerDeceleration = playerDeceleration;
            TargetX = targetX; TargetY = targetY;
            TargetEndX = targetEndX; TargetEndY = targetEndY;
            TargetSpeed = targetSpeed; TargetWantedSpeed = targetWantedSpeed;
            TargetAcceleration = targetAcceleration;
            StandoffDistance = Math.Max(0d, standoffDistance);
            TrajectoryKind = trajectoryKind;
            OrbitDirection = orbitDirection;
            StopAtIntercept = stopAtIntercept;
        }

        internal bool IsFinite => Finite(PlayerX) && Finite(PlayerY)
            && Finite(PlayerVelocityX) && Finite(PlayerVelocityY)
            && Finite(PlayerMaxSpeed) && Finite(PlayerAcceleration)
            && Finite(PlayerDeceleration) && Finite(TargetX) && Finite(TargetY)
            && Finite(TargetEndX) && Finite(TargetEndY) && Finite(TargetSpeed)
            && Finite(TargetWantedSpeed) && Finite(TargetAcceleration)
            && Finite(StandoffDistance) && Finite(OrbitDirection);
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }

    public readonly struct InterceptSolution {
        public readonly double Ticks;
        public readonly double TargetX;
        public readonly double TargetY;
        public InterceptSolution(double ticks, double targetX, double targetY) {
            Ticks = ticks; TargetX = targetX; TargetY = targetY;
        }
        internal bool IsFinite => Ticks >= 0d && !double.IsNaN(Ticks)
            && !double.IsInfinity(Ticks) && !double.IsNaN(TargetX)
            && !double.IsInfinity(TargetX) && !double.IsNaN(TargetY)
            && !double.IsInfinity(TargetY);
    }
}
