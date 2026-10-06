using UnityEngine;
using System.Collections.Generic;

namespace RoboRumble.Training
{
    // Uses the same movement inputs as a human. Never changes inventory or scoring.
    [DefaultExecutionOrder(-100)]
    public class PolicyDriver : MonoBehaviour
    {
        public PolicyModel model;
        private SwerveController drive;
        private RobotShooter shooter;
        private Transform hub;
        private Ball target;
        private float scanAt;
        private Intake[] intakes;
        private Climber climber;
        private ClimbingUI climbUI;
        private ClimbingZone climbZone;
        private GameTimer timer;
        private bool climbMode;
        private Rigidbody body;
        private float cruiseSpeed;
        private FieldNavigator navigator;
        private readonly Dictionary<Ball, float> avoidUntil = new Dictionary<Ball, float>();
        private float progressAt;
        private float closestTargetDistance = float.PositiveInfinity;
        private bool shootingBatch;
        private Vector2 volleyPose;
        private Ball volleyBall;
        private string routeDiagnostic = "";
        public int ThreeBallVolleys { get; private set; }
        public string TargetDescription { get { return (climbMode ? "climb " + climbUI.CurrentHeight : shootingBatch ? "volley " + volleyPose.ToString("F2") : target == null ? "none" : target.transform.position.ToString("F2")) + routeDiagnostic; } }

        private void Start()
        {
            model.Validate();
            drive = GetComponent<SwerveController>();
            cruiseSpeed = drive.maxDriveSpeed;
            shooter = GetComponent<RobotShooter>();
            body = GetComponent<Rigidbody>();
            navigator = new FieldNavigator(GetComponent<BoxCollider>());
            intakes = GetComponentsInChildren<Intake>();
            foreach (Intake intake in intakes) intake.OnBallIntaked += OnCargoIntaked;
            hub = GameObject.Find("TargetTransform").transform;
            climber = GetComponent<Climber>();
            timer = FindObjectOfType<GameTimer>();
            if (climber != null)
            {
                foreach (ClimbingUI ui in FindObjectsOfType<ClimbingUI>())
                    if (ui.TeamColor == climber.robotColor) climbUI = ui;
                foreach (ClimbingZone zone in FindObjectsOfType<ClimbingZone>())
                    if (zone.zoneColor == climber.robotColor && zone.zoneBar == climber.startingHeight) climbZone = zone;
            }
        }

        public int OutsideDoublePickups { get; private set; }
        public int EdgeVolleys { get; private set; }
        public int EdgePickups { get; private set; }
        private bool collectingOutside;
        private bool edgeVolley;
        private float batchProgressAt;
        private float bestBatchDistance;
        private bool volleyParked;
        private float lastShotAt;
        private int observedShots;
        private int observedRollingChain;
        private Ball[] cargo = new Ball[0];
        private bool edgePickup;
        private bool shootAlignedPickup;
        private Ball sweepBall;
        private Vector2 sweepDirection, sweepStart, sweepGoal;
        private float sweepAt, sweepStartRadius;
        private bool sweepPushing, sweptThisVolley;
        public int SweepsCompleted { get; private set; }
        public float SweepMeters { get; private set; }
        public int SecondPickups { get; private set; }
        public int AlignedSecondPickups { get; private set; }
        public float SecondPickupAimErrorSum { get; private set; }
        public string StateLabel { get { return climbMode ? "climb" : sweepBall != null ? sweepPushing ? "push cargo inward" : "approach sweep" : shootingBatch ? edgeVolley ? "edge volley" : "volley" : shootAlignedPickup ? "collect facing hub" : edgePickup ? "edge pickup" : collectingOutside ? "collect two outside range" : "collect cargo"; } }

        private void Update()
        {
            if (!drive.robotEnabled) { drive.OnMove(Vector2.zero); drive.OnRotate(0); return; }
            float[] w = model.weights;
            Vector2 position = transform.position;
            Vector2 hubPosition = hub.position;
            Vector2 hubDelta = position - hubPosition;
            if (climbZone != null && climbUI != null && timer.gameState == GameTimer.GameState.Endgame &&
                w[9] > 0 && timer.RemainingMatchSeconds <= w[9] +
                    1.3f * Vector2.Distance(position, climbZone.GetComponent<Collider>().bounds.center) / Mathf.Max(1f, cruiseSpeed)) climbMode = true;
            if (climbMode)
            {
                if (sweepBall != null) EndSweep(hubPosition);
                Vector2 climbDestination = climbZone.GetComponent<Collider>().bounds.center;
                MoveToward(climber.IsInClimbArea || climber.IsClimbing ? Vector2.zero : navigator.Waypoint(position, climbDestination) - position, w[2], w[7]);
                drive.OnRotate(0);
                return;
            }

            if (target != null && !shootingBatch)
            {
                float distance = Vector2.Distance(position, target.transform.position);
                if (distance < closestTargetDistance - .15f) { closestTargetDistance = distance; progressAt = Time.time; }
                if (Time.time - progressAt > 3f)
                {
                    avoidUntil[target] = Time.time + 12f;
                    target = null;
                    closestTargetDistance = float.PositiveInfinity;
                    progressAt = Time.time;
                    scanAt = 0;
                }
            }
            else { closestTargetDistance = float.PositiveInfinity; progressAt = Time.time; }
            if (Time.time >= scanAt || target != null && !Eligible(target)) ScanCargo(position, hubPosition, w);

            bool rollingShooter = shooter.CanShootMoving && shooter.HasFullTurret;
            float aimedDistance = rollingShooter ? shooter.DistanceToTarget :
                shooter.ShotDistanceAtPose(position, ShootingHeading(position, hubPosition));
            bool beyondRange = aimedDistance > shooter.MaximumShootDistance - w[0];
            if (rollingShooter)
            {
                int chain = shooter.ConsecutiveShots;
                if (chain / 3 > observedRollingChain / 3) ThreeBallVolleys += chain / 3 - observedRollingChain / 3;
                observedRollingChain = chain;
            }
            if (!shootingBatch && shooter.HasActivatedShooter && beyondRange && shooter.ballCount < 2 && target != null)
                collectingOutside = true;
            if (collectingOutside && shooter.ballCount >= 2) { OutsideDoublePickups++; collectingOutside = false; }
            if (target == null) collectingOutside = false;
            bool loaded = shooter.ballCount >= (!shooter.HasActivatedShooter || collectingOutside ? 2 : Mathf.RoundToInt(w[1]));

            if (shootingBatch && shooter.ballCount == 0)
            {
                if (!rollingShooter && shooter.ConsecutiveShots >= 3) ThreeBallVolleys++;
                shootingBatch = false;
            }
            // Full-turret robots can preserve their charge while driving to the next cargo.
            // Outside range they still return with two and enter at the closest useful edge.
            if (shootingBatch && rollingShooter && !beyondRange) shootingBatch = false;
            if (sweepBall != null && (!shootingBatch || shooter.ballCount < 2 || !beyondRange)) EndSweep(hubPosition);
            if (!shootingBatch && shooter.HasActivatedShooter && (!rollingShooter || beyondRange) &&
                (loaded || target == null && shooter.ballCount > 0))
                BeginVolley(position, hubPosition, w);

            if (shootingBatch && beyondRange && shooter.ballCount >= 2 && volleyBall == null && !sweptThisVolley)
                PlanSweep(position, hubPosition, w);

            Vector2 destination;
            edgePickup = false;
            shootAlignedPickup = false;
            if (shootingBatch)
            {
                float remaining = Vector2.Distance(position, volleyPose);
                if (shooter.ShotsFired != observedShots) { observedShots = shooter.ShotsFired; lastShotAt = Time.time; }
                bool thirdReady = volleyBall == null || BallInIntakeReach(volleyBall) &&
                    Mathf.Abs(Mathf.DeltaAngle(transform.eulerAngles.z, ShootingHeading(position, hubPosition))) < 8f;
                if (sweepBall == null && remaining < .12f && thirdReady && aimedDistance < shooter.MaximumShootDistance - .025f &&
                    (w[21] <= 0 || shooter.DistanceToTarget < shooter.MaximumShootDistance - .025f))
                    volleyParked = true;
                if (remaining < bestBatchDistance - .1f) { bestBatchDistance = remaining; batchProgressAt = Time.time; }
                if (remaining > .12f && Time.time - batchProgressAt > 3f || Time.time - lastShotAt > 4f)
                {
                    if (volleyBall != null) avoidUntil[volleyBall] = Time.time + 12f;
                    volleyBall = null;
                    volleyPose = ShootingPose(position, hubPosition, w, out edgeVolley);
                    bestBatchDistance = float.PositiveInfinity;
                    batchProgressAt = Time.time;
                    lastShotAt = Time.time;
                    volleyParked = false;
                }
                destination = volleyPose;
                if (sweepBall != null)
                {
                    Vector2 ballPosition = sweepBall.transform.position;
                    if (!Eligible(sweepBall) || Time.time - sweepAt > 2.5f ||
                        Vector2.Dot(ballPosition - position, sweepDirection) < -.2f ||
                        Mathf.Abs(Vector2.Dot(ballPosition - sweepStart, new Vector2(-sweepDirection.y, sweepDirection.x))) > .45f ||
                        Vector2.Distance(position, sweepGoal) < .15f)
                        EndSweep(hubPosition);
                    else
                    {
                        if (!sweepPushing && Vector2.Distance(position, sweepStart) < .2f) sweepPushing = true;
                        destination = sweepPushing ? sweepGoal : sweepStart;
                        // Give the short physical push its own time budget before resuming the volley.
                        batchProgressAt = lastShotAt = Time.time;
                        bestBatchDistance = float.PositiveInfinity;
                    }
                }
            }
            else if (target != null)
            {
                destination = (Vector2)target.transform.position + (Vector2)target.GetComponent<Rigidbody>().velocity * w[3];
                Vector2 alignedPose;
                if (TryAlignedPickupPose(target, hubPosition, w, out alignedPose))
                {
                    Vector2 radial = ((Vector2)target.transform.position - hubPosition).normalized;
                    Vector2 offset = position - (Vector2)target.transform.position;
                    if (Vector2.Dot(offset, radial) < -.64f && Vector2.Angle(offset, -radial) < 35f)
                    {
                        shootAlignedPickup = true;
                        destination = alignedPose;
                    }
                    else
                    {
                        Vector2 entry = (Vector2)target.transform.position - radial * 1.05f;
                        if (navigator.IsPoseFree(entry, shooter.BodyHeadingForAimAt(entry)))
                        {
                            float routeX, routeY;
                            PlanarRoute.Waypoint(position.x, position.y, entry.x, entry.y,
                                target.transform.position.x, target.transform.position.y, .85f, out routeX, out routeY);
                            shootAlignedPickup = true;
                            destination = new Vector2(routeX, routeY);
                        }
                    }
                }
                if (!shootAlignedPickup && !navigator.IsFree(target.transform.position))
                {
                    edgePickup = TryPickupPose(target, position, out destination);
                    if (!edgePickup)
                    {
                        avoidUntil[target] = Time.time + 12f;
                        target = null;
                        scanAt = 0;
                        destination = position;
                    }
                }
                // Drive through an aligned intake contact. Stopping short costs time and
                // lets the automatic shooter interrupt collection before the second ball.
                // The target changes immediately when the ordinary intake disables its collider.
            }
            else destination = position;

            Vector2 waypoint = navigator.Waypoint(position, destination);
            if (shootAlignedPickup && Vector2.Distance(position, destination) < 1.2f &&
                Mathf.Abs(Mathf.DeltaAngle(transform.eulerAngles.z, shooter.BodyHeadingForAimAt(position))) < 10f &&
                navigator.ClearPose(position, destination, transform.eulerAngles.z)) waypoint = destination;
            if (sweepBall != null && Vector2.Distance(position, destination) < 1.2f &&
                Mathf.Abs(IntakeAngle(-sweepDirection)) < 10f &&
                navigator.ClearPose(position, destination, transform.eulerAngles.z)) waypoint = destination;
            if (shootingBatch && Vector2.Distance(position, destination) < 1.2f &&
                Mathf.Abs(Mathf.DeltaAngle(transform.eulerAngles.z, ShootingHeading(position, hubPosition))) < 10f &&
                navigator.ClearPose(position, destination, transform.eulerAngles.z)) waypoint = destination;
            if (edgePickup && Vector2.Distance(position, destination) < 1.2f &&
                Mathf.Abs(IntakeAngle((Vector2)target.transform.position - position)) < 10f &&
                navigator.ClearPose(position, destination, transform.eulerAngles.z)) waypoint = destination;
            Vector2 movement = waypoint - position;
            Vector2 facing = target != null ? (Vector2)target.transform.position - position : hubPosition - position;
            float angle = IntakeAngle(facing);
            if (shootAlignedPickup) angle = Mathf.DeltaAngle(transform.eulerAngles.z, shooter.BodyHeadingForAimAt(position));
            bool parked = !rollingShooter && shootingBatch && volleyParked;
            if (shootingBatch)
            {
                angle = sweepBall != null ? IntakeAngle(-sweepDirection) : rollingShooter ? IntakeAngle(facing) : VolleyTurn(position, hubPosition, w);
                if (parked) movement = Vector2.zero;
            }
            else if (!shootAlignedPickup && target != null && facing.magnitude < 2f)
            {
                if (Mathf.Abs(angle) > w[13] && facing.magnitude < 1.3f) movement = -facing.normalized * .25f;
                else movement *= Mathf.Clamp01(1f - Mathf.Abs(angle) / w[14]);
            }
            // A full-turret robot keeps advancing while its ordinary shooter opens cargo slots.
            // If it has reached its immediate goal, circulate inside range instead of parking.
            if (rollingShooter && movement.magnitude < .15f && sweepBall == null)
                movement = navigator.Waypoint(position, CruisePose(position, hubPosition, w[0])) - position;
            routeDiagnostic = " | state=" + StateLabel + " aimedRange=" + aimedDistance.ToString("F2") +
                " waypoint=" + waypoint.ToString("F2") + (edgePickup ? " pickupPose=" + destination.ToString("F2") : "") +
                (sweepBall != null ? " sweepCargo=" + sweepBall.transform.position.ToString("F2") : "") +
                " progressAge=" + (Time.time - progressAt).ToString("F1");
            MoveToward(movement, w[2], w[7]);
            float desiredTurnSpeed = Mathf.Clamp(angle * Mathf.Deg2Rad * w[6], -drive.MaximumTurnSpeed, drive.MaximumTurnSpeed);
            drive.OnRotate(Mathf.Abs(angle) < 1f && Mathf.Abs(body.angularVelocity.z) < .1f ? 0 :
                Mathf.Clamp((desiredTurnSpeed - body.angularVelocity.z) * .5f, -1f, 1f));
        }

        private bool Eligible(Ball ball)
        {
            float retryAt;
            if (ball == null || avoidUntil.TryGetValue(ball, out retryAt) && Time.time < retryAt) return false;
            var c = ball.GetComponent<SphereCollider>();
            var rb = ball.GetComponent<Rigidbody>();
            return ball.ballColor == shooter.ballColor && c != null && c.enabled && rb != null && !rb.isKinematic &&
                ball.transform.position.z >= -.05f && navigator.CanApproach(ball.transform.position);
        }

        private void OnCargoIntaked(bool value)
        {
            if (edgePickup) EdgePickups++;
            if (!shooter.CanShootMoving && !shootingBatch && shooter.ballCount == 2)
            {
                SecondPickups++;
                float error = Mathf.Abs(shooter.BodyAimError);
                SecondPickupAimErrorSum += error;
                if (error <= shooter.BodyAimToleranceDegrees) AlignedSecondPickups++;
            }
        }

        private bool BallInIntakeReach(Ball ball)
        {
            var sphere = ball.GetComponent<SphereCollider>();
            float radius = sphere.radius * ball.transform.lossyScale.x;
            foreach (Intake intake in intakes)
            {
                var box = intake.GetComponent<BoxCollider>();
                if (box != null && Vector3.Distance(box.ClosestPoint(ball.transform.position), ball.transform.position) < radius - .005f)
                    return true;
            }
            return false;
        }

        private bool TryPickupPose(Ball ball, Vector2 position, out Vector2 pose)
        {
            pose = position;
            float best = float.PositiveInfinity;
            float ballRadius = ball.GetComponent<SphereCollider>().radius * ball.transform.lossyScale.x;
            foreach (Intake intake in intakes)
            {
                var box = intake.GetComponent<BoxCollider>();
                if (box == null) continue;
                Vector2 offset = intake.transform.position - transform.position;
                float reach = offset.magnitude + Mathf.Min(box.size.x * intake.transform.lossyScale.x,
                    box.size.y * intake.transform.lossyScale.y) * .5f + ballRadius * .45f;
                for (int n = 0; n < 24; n++)
                {
                    Vector2 direction = Quaternion.Euler(0, 0, n * 15f) * Vector2.right;
                    Vector2 candidate = (Vector2)ball.transform.position - direction * reach;
                    float turn = Vector2.SignedAngle(offset, direction);
                    float heading = transform.eulerAngles.z + turn;
                    if (!navigator.IsPoseFree(candidate, heading)) continue;
                    float cost = Vector2.Distance(position, candidate) + Mathf.Abs(turn) / 360f;
                    if (cost < best) { best = cost; pose = candidate; }
                }
            }
            return !float.IsPositiveInfinity(best);
        }

        private void ScanCargo(Vector2 position, Vector2 hubPosition, float[] w)
        {
            scanAt = Time.time + .1f;
            cargo = FindObjectsOfType<Ball>();
            var available = new List<Ball>();
            foreach (Ball ball in cargo) if (Eligible(ball)) available.Add(ball);
            Ball next = null;
            float best = float.PositiveInfinity;
            foreach (Ball ball in available)
            {
                Vector2 delta = (Vector2)ball.transform.position - position;
                float firstTurn = IntakeAngle(delta);
                float cost = delta.magnitude + w[4] * Vector2.Distance(ball.transform.position, hubPosition) +
                    w[5] * Mathf.Abs(firstTurn) / 180f +
                    TurnTravelPenalty(firstTurn, delta.magnitude, drive.maxDriveSpeed, w[18]);
                Vector2 alignedPose;
                if (TryAlignedPickupPose(ball, hubPosition, w, out alignedPose))
                {
                    Vector2 radial = ((Vector2)ball.transform.position - hubPosition).normalized;
                    float sideAngle = Vector2.Angle(position - (Vector2)ball.transform.position, -radial);
                    float travel = Vector2.Distance(position, alignedPose) + 1.7f * Mathf.Max(0f, sideAngle - 35f) / 180f;
                    float hubTurn = Mathf.DeltaAngle(transform.eulerAngles.z, shooter.BodyHeadingForAimAt(position));
                    cost = travel + w[4] * Vector2.Distance(ball.transform.position, hubPosition) +
                        w[5] * Mathf.Abs(hubTurn) / 180f + TurnTravelPenalty(hubTurn, travel, drive.maxDriveSpeed, w[18]);
                }
                if (!shootingBatch && shooter.ballCount == 1)
                    cost += DeliveryTurnPenalty(position, ball.transform.position, w[19]);
                bool outside = shooter.ShotDistanceAtPose(ball.transform.position, ShootingHeading(ball.transform.position, hubPosition)) >
                    shooter.MaximumShootDistance - w[0];
                if (shooter.CanShootMoving && shooter.HasFullTurret && shooter.HasActivatedShooter && shooter.ballCount > 0 &&
                    shooter.DistanceToTarget <= shooter.MaximumShootDistance - w[0])
                {
                    float pickupSeconds = Mathf.Max(Mathf.Max(0, delta.magnitude - .65f) / Mathf.Max(.1f, drive.maxDriveSpeed),
                        Mathf.Abs(IntakeAngle(delta)) * Mathf.Deg2Rad / drive.MaximumTurnSpeed);
                    float supplySeconds = shooter.SecondsUntilNextShot + (shooter.ballCount - 1) * shooter.ShotIntervalSeconds;
                    // Prefer a refill before the magazine empties, or a route that preserves range.
                    if (outside || pickupSeconds > supplySeconds)
                        cost += w[17] * shooter.SpinUpSeconds * cruiseSpeed;
                }
                if (!shootingBatch && shooter.ballCount == 0 && (outside || Mathf.RoundToInt(w[1]) >= 2) && available.Count > 1)
                {
                    float secondDistance = float.PositiveInfinity;
                    foreach (Ball second in available)
                        if (second != ball)
                        {
                            float separation = Vector2.Distance(ball.transform.position, second.transform.position);
                            separation += DeliveryTurnPenalty(ball.transform.position, second.transform.position, w[19]);
                            if (w[18] > 0)
                            {
                                Vector2 secondDelta = second.transform.position - ball.transform.position;
                                float secondTurn = 180f;
                                foreach (Intake intake in intakes)
                                {
                                    Vector2 futureIntake = Quaternion.Euler(0, 0, firstTurn) *
                                        (intake.transform.position - transform.position);
                                    secondTurn = Mathf.Min(secondTurn, Mathf.Abs(Vector2.SignedAngle(futureIntake, secondDelta)));
                                }
                                separation += TurnTravelPenalty(secondTurn, secondDelta.magnitude,
                                    shooter.CanShootMoving ? shooter.ShootingDriveSpeed : cruiseSpeed, w[18]);
                            }
                            // In an outside cluster, visit the outer ball first and collect inward.
                            if (outside && shooter.CanShootMoving && w[15] > 0)
                                separation += 2f * Mathf.Max(0, Vector2.Distance(second.transform.position, hubPosition) -
                                    Vector2.Distance(ball.transform.position, hubPosition));
                            secondDistance = Mathf.Min(secondDistance, separation);
                        }
                    cost += w[12] * secondDistance;
                }
                if (ball == target) cost -= w[8];
                if (cost < best) { best = cost; next = ball; }
            }
            if (target != next) { progressAt = Time.time; closestTargetDistance = float.PositiveInfinity; }
            target = next;
        }

        private bool TryAlignedPickupPose(Ball ball, Vector2 hubPosition, float[] w, out Vector2 pose)
        {
            pose = ball.transform.position;
            if (w[20] < .5f || shooter.CanShootMoving || ((Vector2)ball.GetComponent<Rigidbody>().velocity).magnitude > .5f)
                return false;
            Vector2 radial = (pose - hubPosition).normalized;
            pose -= radial * w[11];
            float heading = shooter.BodyHeadingForAimAt(pose);
            return shooter.ShotDistanceAtPose(pose, heading) <= shooter.MaximumShootDistance - w[0] &&
                navigator.IsPoseFree(pose, heading);
        }

        private float TurnTravelPenalty(float angle, float distance, float speed, float strength)
        {
            // Express turning time that cannot fit before the close approach as travel distance.
            // Turning during the longer part of a trip remains free in this estimate.
            float rotationDistance = Mathf.Abs(angle) * Mathf.Deg2Rad * speed / drive.MaximumTurnSpeed;
            return strength * Mathf.Max(0f, rotationDistance - Mathf.Max(0f, distance - 1.3f));
        }

        private float DeliveryTurnPenalty(Vector2 from, Vector2 pickup, float strength)
        {
            if (shooter.CanShootMoving || strength <= 0) return 0;
            Vector2 approach = pickup - from;
            if (approach.sqrMagnitude < .01f) return 0;
            float arrivalHeading = transform.eulerAngles.z + IntakeAngle(approach);
            Vector2 arrivalPosition = pickup - approach.normalized * .65f;
            float aimHeading = shooter.BodyHeadingForAimAt(arrivalPosition);
            float turn = Mathf.Max(0f, Mathf.Abs(Mathf.DeltaAngle(arrivalHeading, aimHeading)) - shooter.BodyAimToleranceDegrees);
            // A stationary shooter must finish this turn before its ordinary charge can build.
            return strength * turn * Mathf.Deg2Rad * cruiseSpeed / drive.MaximumTurnSpeed;
        }

        private float VolleyTurn(Vector2 position, Vector2 hubPosition, float[] w)
        {
            float centerTurn = Mathf.DeltaAngle(transform.eulerAngles.z, ShootingHeading(position, hubPosition));
            // A third cargo needs the rear intake precisely lined up. Ordinary two-ball
            // stops can use the existing turret envelope, with five degrees to spare.
            float allowance = volleyBall != null ? 0 : w[21] * Mathf.Max(0, shooter.BodyAimToleranceDegrees - 5f);
            return centerTurn - Mathf.Clamp(centerTurn, -allowance, allowance);
        }

        private float ShootingHeading(Vector2 position, Vector2 hubPosition)
        {
            if (!shooter.HasFullTurret) return shooter.BodyHeadingForAimAt(position);
            Vector2 radial = shooter.CanShootMoving && target != null ? (Vector2)target.transform.position - position : position - hubPosition;
            return transform.eulerAngles.z + IntakeAngle(radial);
        }

        private Vector2 RangeEdge(Vector2 radial, Vector2 hubPosition, float margin)
        {
            radial = radial.sqrMagnitude < .001f ? Vector2.right : radial.normalized;
            float low = 0, high = shooter.MaximumShootDistance + 2f;
            for (int n = 0; n < 12; n++)
            {
                float radius = (low + high) * .5f;
                Vector2 position = hubPosition + radial * radius;
                float distance = shooter.ShotDistanceAtPose(position, ShootingHeading(position, hubPosition));
                if (distance <= shooter.MaximumShootDistance - margin) low = radius;
                else high = radius;
            }
            return hubPosition + radial * low;
        }

        private Vector2 CruisePose(Vector2 position, Vector2 hubPosition, float margin)
        {
            Vector2 radial = (position - hubPosition).normalized;
            Vector2 preferred = shooter.DistanceToTarget > shooter.MaximumShootDistance - margin ?
                -radial : new Vector2(-radial.y, radial.x);
            for (int n = 0; n < 12; n++)
            {
                Vector2 direction = Quaternion.Euler(0, 0, n * 30f) * preferred;
                Vector2 pose = position + direction * .8f;
                if (navigator.IsFree(pose) && navigator.ClearPose(position, pose, transform.eulerAngles.z) &&
                    shooter.ShotDistanceAtPose(pose, ShootingHeading(pose, hubPosition)) < shooter.MaximumShootDistance - margin)
                    return pose;
            }
            return position;
        }

        private Vector2 ShootingPose(Vector2 position, Vector2 hubPosition, float[] w, out bool useEdge)
        {
            bool currentOutside = shooter.ShotDistanceAtPose(position, ShootingHeading(position, hubPosition)) > shooter.MaximumShootDistance - w[0];
            bool nextOutside = target != null && shooter.ShotDistanceAtPose(target.transform.position,
                ShootingHeading(target.transform.position, hubPosition)) > shooter.MaximumShootDistance - w[0];
            useEdge = currentOutside || nextOutside;
            if (!useEdge && navigator.IsFree(position)) return position;
            Vector2 radial = nextOutside ? (Vector2)target.transform.position - hubPosition : position - hubPosition;
            Vector2 bestPose = position;
            float best = float.PositiveInfinity;
            for (int n = -5; n <= 5; n++)
            {
                Vector2 direction = Quaternion.Euler(0, 0, n * 8f) * radial;
                Vector2 edge = RangeEdge(direction, hubPosition, w[0]);
                float radius = Vector2.Distance(edge, hubPosition);
                for (int inward = 0; inward < 10 && radius - inward * .3f > 1.9f; inward++)
                {
                    Vector2 pose = hubPosition + direction.normalized * (radius - inward * .3f);
                    if (!navigator.IsFree(pose)) continue;
                    float cost = Vector2.Distance(position, pose);
                    if (nextOutside) cost += Vector2.Distance(pose, target.transform.position);
                    if (cost < best) { best = cost; bestPose = pose; }
                    break;
                }
            }
            return bestPose;
        }

        private void BeginVolley(Vector2 position, Vector2 hubPosition, float[] w)
        {
            if (sweepBall != null) EndSweep(hubPosition);
            sweptThisVolley = false;
            shootingBatch = true;
            collectingOutside = false;
            volleyPose = ShootingPose(position, hubPosition, w, out edgeVolley);
            volleyBall = null;
            float best = w[10];
            if (shooter.ballCount >= 2 && !shooter.CanShootMoving)
                foreach (Ball ball in cargo)
                {
                    if (!Eligible(ball) || ((Vector2)ball.GetComponent<Rigidbody>().velocity).magnitude > .5f) continue;
                    Vector2 radial = (Vector2)ball.transform.position - hubPosition;
                    Vector2 pose = (Vector2)ball.transform.position - radial.normalized * w[11];
                    float cost = Vector2.Distance(position, pose);
                    if (cost >= best || !navigator.IsPoseFree(pose, ShootingHeading(pose, hubPosition)) ||
                        shooter.ShotDistanceAtPose(pose, ShootingHeading(pose, hubPosition)) > shooter.MaximumShootDistance - w[0]) continue;
                    best = cost;
                    volleyPose = pose;
                    volleyBall = ball;
                    edgeVolley = false;
                }
            if (edgeVolley) EdgeVolleys++;
            batchProgressAt = Time.time;
            bestBatchDistance = float.PositiveInfinity;
            volleyParked = false;
            lastShotAt = Time.time;
            observedShots = shooter.ShotsFired;
        }

        private void PlanSweep(Vector2 position, Vector2 hubPosition, float[] w)
        {
            sweptThisVolley = true;
            if (w[15] <= .01f) return;
            float best = float.PositiveInfinity;
            foreach (Ball ball in cargo)
            {
                if (!Eligible(ball) || ((Vector2)ball.GetComponent<Rigidbody>().velocity).magnitude > .5f) continue;
                Vector2 point = ball.transform.position;
                Vector2 inward = (hubPosition - point).normalized;
                if (shooter.ShotDistanceAtPose(point, ShootingHeading(point, hubPosition)) <= shooter.MaximumShootDistance - w[0]) continue;
                for (int side = -2; side <= 2; side++)
                {
                    // A diagonal push can reach a pile whose straight outer approach is blocked.
                    Vector2 direction = Quaternion.Euler(0, 0, side * 30f) * inward;
                    Vector2 start = point - direction * .85f;
                    Vector2 goal = point + direction * w[16];
                    float heading = transform.eulerAngles.z + IntakeAngle(-direction);
                    float detour = Vector2.Distance(position, start) + Vector2.Distance(start, volleyPose) - Vector2.Distance(position, volleyPose);
                    if (detour > w[15] || Vector2.Dot(point - position, direction) < .2f ||
                        !navigator.IsPoseFree(start, heading) || !navigator.IsPoseFree(goal, heading) ||
                        !navigator.ClearPose(start, goal, heading)) continue;
                    float cost = detour + Vector2.Distance(position, start) + Mathf.Abs(side) * .15f;
                    if (cost >= best) continue;
                    best = cost;
                    sweepBall = ball;
                    sweepDirection = direction;
                    sweepStart = start;
                    sweepGoal = goal;
                    sweepStartRadius = Vector2.Distance(point, hubPosition);
                    sweepAt = Time.time;
                    sweepPushing = false;
                }
            }
        }

        private void EndSweep(Vector2 hubPosition)
        {
            if (sweepBall != null)
            {
                float moved = sweepStartRadius - Vector2.Distance(sweepBall.transform.position, hubPosition);
                if (moved > .2f) { SweepsCompleted++; SweepMeters += moved; }
            }
            sweepBall = null;
            sweepPushing = false;
            batchProgressAt = lastShotAt = Time.time;
            bestBatchDistance = float.PositiveInfinity;
        }
        private void MoveToward(Vector2 delta, float gain, float inputLimit)
        {
            float desiredSpeed = Mathf.Min(drive.maxDriveSpeed, delta.magnitude * gain);
            float speed = ((Vector2)body.velocity).magnitude;
            // Swerve input magnitude controls acceleration, not a speed setpoint. Release to brake.
            drive.OnMove(delta.magnitude < .05f || speed > desiredSpeed + .15f ? Vector2.zero : delta.normalized * inputLimit);
        }

        private float IntakeAngle(Vector2 destination)
        {
            float result = Vector2.SignedAngle(transform.up, destination);
            bool found = false;
            foreach (Intake intake in intakes)
            {
                Vector2 direction = intake.transform.position - transform.position;
                if (direction.sqrMagnitude < .001f) continue;
                float angle = Vector2.SignedAngle(direction, destination);
                if (!found || Mathf.Abs(angle) < Mathf.Abs(result)) { result = angle; found = true; }
            }
            return result;
        }

        private void LateUpdate()
        {
            // The same fire input as a player, timed against the visible climb indicator.
            if (climbMode && climbUI.CanPressClimb) climber.OnFire(1);
        }

        private void OnDisable()
        {
            if (intakes != null) foreach (Intake intake in intakes) intake.OnBallIntaked -= OnCargoIntaked;
            if (drive != null) { drive.OnMove(Vector2.zero); drive.OnRotate(0); }
        }
    }
}


