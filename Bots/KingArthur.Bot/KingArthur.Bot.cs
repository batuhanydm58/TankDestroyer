using System;
using System.Collections.Generic;
using System.Linq;
using TankDestroyer.API;

namespace KingArthur.Bot;

[Bot("KingArthur", "Batuhan", "#eff317")]
public class KingArthurBot : IPlayerBot
{
    private static readonly Direction[] Moves =
    {
        Direction.North,
        Direction.South,
        Direction.East,
        Direction.West
    };

    private readonly Random _random = new();

    private Direction? _lastMove;
    private int _turn;

    private int _enemyLastX;
    private int _enemyLastY;
    private int _enemyLastDistance;
    private bool _hasEnemyMemory;

    private int _enemyAggression;
    private int _enemyPassivity;

    public void DoTurn(ITurnContext ctx)
    {
        _turn++;

        var me = ctx.Tank;
        if (me.Destroyed)
            return;

        var enemies = ctx.GetTanks()
            .Where(t => !t.Destroyed && t.OwnerId != me.OwnerId)
            .ToArray();

        if (enemies.Length == 0)
            return;

        // This bot is tuned for 1v1.
        // If FFA happens anyway, target weakest and still behaves decently.
        var enemy = enemies
            .OrderBy(e => e.Health)
            .ThenBy(e => Distance(me.X, me.Y, e.X, e.Y))
            .First();

        UpdateEnemyProfile(me, enemy);

        var map = AnalyzeMap(ctx);

        var best = BuildCandidates(ctx, me, enemies, enemy, map)
            .OrderByDescending(c => c.Score)
            .First();

        if (best.Move.HasValue)
        {
            ctx.MoveTank(best.Move.Value);
            _lastMove = best.Move.Value;
        }

        var target = best.Target ?? PickTarget(ctx, best.X, best.Y, enemies);

        var aim = best.Target == null
            ? AimApproximately(best.X, best.Y, target.X, target.Y)
            : AimExactly(best.X, best.Y, target.X, target.Y);

        if ((int)aim != 0)
            ctx.RotateTurret(aim);

        if (Tile(ctx, best.X, best.Y) != TileType.Tree)
            ctx.Fire();
    }

    private List<Candidate> BuildCandidates(
        ITurnContext ctx,
        ITank me,
        ITank[] enemies,
        ITank mainEnemy,
        MapInfo map)
    {
        var candidates = new List<Candidate>
        {
            ScoreCandidate(ctx, me, enemies, mainEnemy, map, null, me.X, me.Y)
        };

        foreach (var move in Moves)
        {
            var next = AfterMove(me.X, me.Y, move);

            if (CanMoveTo(ctx, next.X, next.Y))
            {
                candidates.Add(ScoreCandidate(ctx, me, enemies, mainEnemy, map, move, next.X, next.Y));
            }
        }

        return candidates;
    }

    private Candidate ScoreCandidate(
        ITurnContext ctx,
        ITank me,
        ITank[] enemies,
        ITank mainEnemy,
        MapInfo map,
        Direction? move,
        int x,
        int y)
    {
        var candidate = new Candidate
        {
            Move = move,
            X = x,
            Y = y
        };

        var score = _random.Next(0, 10);

        var tile = Tile(ctx, x, y);
        var distance = Distance(x, y, mainEnemy.X, mainEnemy.Y);
        var healthLead = me.Health - mainEnemy.Health;

        var incomingDamage = IncomingBulletDamage(ctx, x, y);
        if (incomingDamage > 0)
        {
            score -= 200_000;
            score -= incomingDamage * 3_000;
        }

        var shotTarget = BestShotTarget(ctx, x, y, enemies);
        candidate.Target = shotTarget;

        var threat = EnemyThreat(ctx, x, y, enemies);
        var directThreat = enemies.Any(e => CanHit(ctx, e.X, e.Y, x, y));

        score += TileScore(tile, shotTarget != null, map);
        score += Mobility(ctx, me, x, y) * MobilityWeight(map);
        score -= EdgePenalty(ctx, x, y, map);

        if (shotTarget != null)
        {
            score += ShotScore(ctx, me, shotTarget, x, y, threat, map);
        }
        else
        {
            score += NoShotScore(ctx, me, mainEnemy, enemies, x, y, map);
        }

        score += RangeScore(ctx, me, mainEnemy, x, y, distance, map);
        score += OpponentStyleScore(ctx, me, mainEnemy, x, y, distance, map);
        score -= RiskPenalty(ctx, me, threat, directThreat, shotTarget, map);

        // If we are losing, force a real fight.
        if (healthLead <= -25)
        {
            score -= distance * 120;

            if (shotTarget != null)
                score += 5_000;

            if (move == null && shotTarget == null)
                score -= 1_500;
        }

        // If we are winning late, still try to kill, but do not throw.
        if (_turn >= 180 && healthLead >= 20)
        {
            score -= threat.Damage * 120;

            if (shotTarget != null && threat.Damage == 0)
                score += 3_500;

            if (shotTarget == null)
                score += Tile(ctx, x, y) == TileType.Building ? 1_000 : 0;
        }

        // Anti-draw: this is key against Schwarzenegger/Super Robot.
        if (_turn >= 90 && _enemyPassivity >= 5)
        {
            score -= distance * 110;

            if (shotTarget != null)
                score += 4_000;

            if (DistanceToNearestFiringCell(ctx, x, y, enemies) <= 2)
                score += 2_000;

            if (move == null && shotTarget == null)
                score -= 1_500;
        }

        // Avoid sitting in tree if we have a shot.
        if (tile == TileType.Tree && shotTarget != null)
            score -= 8_000;

        if (_lastMove.HasValue && move.HasValue && IsReverse(_lastMove.Value, move.Value))
        {
            score += shotTarget != null ? 250 : -220;
        }

        if (move == null)
        {
            score += shotTarget != null ? 500 : -250;
        }

        candidate.Score = score;
        return candidate;
    }

    private int ShotScore(
        ITurnContext ctx,
        ITank me,
        ITank target,
        int x,
        int y,
        Threat threat,
        MapInfo map)
    {
        var score = 0;

        var damage = DamageAt(ctx, target.X, target.Y);
        var distance = Distance(x, y, target.X, target.Y);
        var killShot = damage >= target.Health;

        score += 10_000;
        score += damage * 110;
        score += (100 - target.Health) * 85;
        score -= distance * 25;

        if (killShot)
            score += 60_000;

        if (target.Health <= 25)
            score += 8_000;
        else if (target.Health <= 50)
            score += 4_000;
        else if (target.Health <= 75)
            score += 1_500;

        // Good trade.
        if (threat.Damage == 0)
        {
            score += 6_000;
        }
        else
        {
            var trade = damage - threat.Damage;
            score += trade * 90;

            if (me.Health <= threat.Damage && !killShot)
                score -= 50_000;

            if (me.Health <= threat.Damage && killShot)
                score -= 12_000;
        }

        if (map.IsOpen && threat.Damage == 0)
            score += 2_500;

        if (map.IsChokeMap && killShot)
            score += 4_000;

        return score;
    }

    private int NoShotScore(
        ITurnContext ctx,
        ITank me,
        ITank mainEnemy,
        ITank[] enemies,
        int x,
        int y,
        MapInfo map)
    {
        var score = 0;

        var distanceToShot = DistanceToNearestFiringCell(ctx, x, y, enemies);

        var forceFight =
            _turn >= 70 ||
            mainEnemy.Health <= 75 ||
            me.Health <= mainEnemy.Health ||
            _enemyPassivity >= 4;

        var pathWeight = forceFight ? 750 : 450;

        if (map.IsChokeMap)
            pathWeight += 180;

        if (map.IsOpen)
            pathWeight += 120;

        score -= Math.Min(distanceToShot, 10) * pathWeight;

        if (IsOnShotLine(x, y, mainEnemy.X, mainEnemy.Y))
        {
            score += 900;
            score -= Distance(x, y, mainEnemy.X, mainEnemy.Y) * 30;
        }

        return score;
    }

    private int RangeScore(
        ITurnContext ctx,
        ITank me,
        ITank enemy,
        int x,
        int y,
        int distance,
        MapInfo map)
    {
        var score = 0;

        // Against aggressive bots like RushBot: keep ideal kill range.
        if (_enemyAggression >= 4)
        {
            if (distance <= 2)
                score -= 3_000;

            if (distance >= 4 && distance <= 7)
                score += 2_000;

            if (CanHit(ctx, x, y, enemy.X, enemy.Y))
                score += 2_000;

            return score;
        }

        // Against passive bots: close distance. Otherwise they draw forever.
        if (_enemyPassivity >= 4)
        {
            score -= distance * 130;

            if (distance <= 6)
                score += 1_300;

            return score;
        }

        if (map.IsOpen)
        {
            if (distance <= 2)
                score -= 1_700;

            if (distance >= 4 && distance <= 6)
                score += 1_200;
        }
        else if (map.IsChokeMap)
        {
            score -= distance * 80;
        }
        else
        {
            if (distance >= 3 && distance <= 7)
                score += 700;

            score -= distance * 35;
        }

        return score;
    }

    private int OpponentStyleScore(
        ITurnContext ctx,
        ITank me,
        ITank enemy,
        int x,
        int y,
        int distance,
        MapInfo map)
    {
        var score = 0;

        // Anti-Blitz / Anti-Rush.
        if (_enemyAggression >= 5)
        {
            var canShootEnemy = CanHit(ctx, x, y, enemy.X, enemy.Y);
            var enemyCanShootUs = CanHit(ctx, enemy.X, enemy.Y, x, y);

            if (canShootEnemy && !enemyCanShootUs)
                score += 4_000;

            if (enemyCanShootUs && !canShootEnemy)
                score -= 3_500;
        }

        // Anti-Schwarzenegger / Anti-Super Robot.
        if (_enemyPassivity >= 5)
        {
            if (Tile(ctx, x, y) == TileType.Building)
                score += 600;

            if (DistanceToNearestFiringCell(ctx, x, y, new[] { enemy }) <= 1)
                score += 2_500;

            if (_turn >= 120)
                score -= distance * 80;
        }

        return score;
    }

    private int RiskPenalty(
        ITurnContext ctx,
        ITank me,
        Threat threat,
        bool directThreat,
        ITank shotTarget,
        MapInfo map)
    {
        var penalty = 0;

        var damageWeight = 120;
        var countWeight = 950;

        if (me.Health <= 50)
        {
            damageWeight += 90;
            countWeight += 600;
        }

        if (me.Health <= 25)
        {
            damageWeight += 170;
            countWeight += 1_500;
        }

        // In open maps you must accept some pressure, otherwise you never win.
        if (map.IsOpen)
            damageWeight -= 25;

        // In choke maps, exposure is usually avoidable. Penalize it harder.
        if (map.IsChokeMap)
            countWeight += 300;

        penalty += threat.Damage * damageWeight;
        penalty += threat.Count * countWeight;

        if (directThreat)
            penalty += 850;

        if (threat.Damage >= me.Health && shotTarget == null)
            penalty += 60_000;

        return penalty;
    }

    private ITank BestShotTarget(ITurnContext ctx, int x, int y, ITank[] enemies)
    {
        ITank best = null;
        var bestScore = int.MinValue;

        foreach (var enemy in enemies)
        {
            if (!CanHit(ctx, x, y, enemy.X, enemy.Y))
                continue;

            var damage = DamageAt(ctx, enemy.X, enemy.Y);
            var distance = Distance(x, y, enemy.X, enemy.Y);

            var score = damage * 120;
            score += (100 - enemy.Health) * 100;
            score -= distance * 25;

            if (damage >= enemy.Health)
                score += 70_000;

            if (enemy.Health <= 25)
                score += 8_000;
            else if (enemy.Health <= 50)
                score += 4_000;

            if (score > bestScore)
            {
                bestScore = score;
                best = enemy;
            }
        }

        return best;
    }

    private ITank PickTarget(ITurnContext ctx, int x, int y, ITank[] enemies)
    {
        return enemies
            .OrderByDescending(e =>
            {
                var score = 0;

                score += (100 - e.Health) * 100;
                score -= Distance(x, y, e.X, e.Y) * 60;

                if (IsOnShotLine(x, y, e.X, e.Y))
                    score += 1_000;

                if (_enemyPassivity >= 4)
                    score -= Distance(x, y, e.X, e.Y) * 50;

                return score;
            })
            .First();
    }

    private Threat EnemyThreat(ITurnContext ctx, int x, int y, ITank[] enemies)
    {
        var damage = 0;
        var count = 0;

        foreach (var enemy in enemies)
        {
            var enemyAlreadyThreatens = CanHit(ctx, enemy.X, enemy.Y, x, y);

            if (enemyAlreadyThreatens)
            {
                damage += DamageAt(ctx, x, y);
                count += 2;
            }

            foreach (var pos in PossibleEnemyPositions(ctx, enemy))
            {
                if (pos.X == enemy.X && pos.Y == enemy.Y)
                    continue;

                if (!CanHit(ctx, pos.X, pos.Y, x, y))
                    continue;

                if (!enemyAlreadyThreatens)
                    damage += DamageAt(ctx, x, y);

                count++;
                break;
            }
        }

        return new Threat
        {
            Damage = damage,
            Count = count
        };
    }

    private IEnumerable<Position> PossibleEnemyPositions(ITurnContext ctx, ITank enemy)
    {
        yield return new Position(enemy.X, enemy.Y);

        foreach (var move in Moves)
        {
            var next = AfterMove(enemy.X, enemy.Y, move);

            if (CanMoveTo(ctx, next.X, next.Y))
                yield return next;
        }
    }

    private int IncomingBulletDamage(ITurnContext ctx, int x, int y)
    {
        var total = 0;

        foreach (var bullet in ctx.GetBullets())
        {
            if (BulletWouldHitCell(ctx, bullet, x, y))
                total += DamageAt(ctx, x, y);
        }

        return total;
    }

    private bool BulletWouldHitCell(ITurnContext ctx, IBullet bullet, int targetX, int targetY)
    {
        foreach (var cell in BulletCells(bullet.X, bullet.Y, bullet.Direction, ctx.GetMapWidth(), ctx.GetMapHeight()))
        {
            if (cell.X == targetX && cell.Y == targetY)
                return true;

            var tile = Tile(ctx, cell.X, cell.Y);

            if (tile == TileType.Tree)
                return false;

            if (tile == TileType.Building && (cell.X != bullet.X || cell.Y != bullet.Y))
                return false;
        }

        return false;
    }

    private int DistanceToNearestFiringCell(ITurnContext ctx, int startX, int startY, ITank[] enemies)
    {
        const int searchLimit = 10;

        if (!Inside(ctx, startX, startY))
            return 99;

        var width = ctx.GetMapWidth();
        var height = ctx.GetMapHeight();

        var visited = new bool[width, height];
        var queue = new Queue<Node>();

        visited[startX, startY] = true;
        queue.Enqueue(new Node(startX, startY, 0));

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            if (current.Depth > searchLimit)
                continue;

            if (enemies.Any(e => CanHit(ctx, current.X, current.Y, e.X, e.Y)))
                return current.Depth;

            foreach (var move in Moves)
            {
                var next = AfterMove(current.X, current.Y, move);

                if (!Inside(ctx, next.X, next.Y))
                    continue;

                if (visited[next.X, next.Y])
                    continue;

                if (!CanMoveTo(ctx, next.X, next.Y))
                    continue;

                visited[next.X, next.Y] = true;
                queue.Enqueue(new Node(next.X, next.Y, current.Depth + 1));
            }
        }

        return 99;
    }

    private bool CanHit(ITurnContext ctx, int fromX, int fromY, int toX, int toY)
    {
        if (fromX == toX && fromY == toY)
            return false;

        if (Tile(ctx, fromX, fromY) == TileType.Tree)
            return false;

        if (!IsOnShotLine(fromX, fromY, toX, toY))
            return false;

        var dx = Math.Abs(toX - fromX);
        var dy = Math.Abs(toY - fromY);
        var range = Math.Max(dx, dy);

        if (dx == dy)
        {
            if (range > 4)
                return false;
        }
        else
        {
            if (range > 6)
                return false;
        }

        return BulletCanReach(ctx, fromX, fromY, AimExactly(fromX, fromY, toX, toY), toX, toY);
    }

    private bool BulletCanReach(ITurnContext ctx, int fromX, int fromY, TurretDirection direction, int toX, int toY)
    {
        foreach (var cell in BulletCells(fromX, fromY, direction, ctx.GetMapWidth(), ctx.GetMapHeight()).Skip(1))
        {
            if (cell.X == toX && cell.Y == toY)
                return true;

            var tile = Tile(ctx, cell.X, cell.Y);

            if (tile == TileType.Tree || tile == TileType.Building)
                return false;
        }

        return false;
    }

    private static IEnumerable<Position> BulletCells(int startX, int startY, TurretDirection direction, int width, int height)
    {
        var vector = DirectionVector(direction);

        var length = Math.Sqrt(vector.X * vector.X + vector.Y * vector.Y);
        if (length <= 0.0001)
            yield break;

        var normalizedX = vector.X / length;
        var normalizedY = vector.Y / length;

        for (var i = 0; i <= 6; i++)
        {
            var x = Clamp((int)(normalizedX * i) + startX, 0, width - 1);
            var y = Clamp((int)(normalizedY * i) + startY, 0, height - 1);

            yield return new Position(x, y);
        }
    }

    private static Position DirectionVector(TurretDirection direction)
    {
        var x = 0;
        var y = 0;

        if (direction.HasFlag(TurretDirection.North))
            y += 1;

        if (direction.HasFlag(TurretDirection.South))
            y -= 1;

        if (direction.HasFlag(TurretDirection.West))
            x += 1;

        if (direction.HasFlag(TurretDirection.East))
            x -= 1;

        return new Position(x, y);
    }

    private MapInfo AnalyzeMap(ITurnContext ctx)
    {
        var width = ctx.GetMapWidth();
        var height = ctx.GetMapHeight();
        var total = Math.Max(1, width * height);

        var water = 0;
        var cover = 0;
        var building = 0;
        var open = 0;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var tile = Tile(ctx, x, y);

                switch (tile)
                {
                    case TileType.Water:
                        water++;
                        break;
                    case TileType.Building:
                        building++;
                        cover++;
                        break;
                    case TileType.Tree:
                        cover++;
                        break;
                    case TileType.Grass:
                    case TileType.Sand:
                        open++;
                        break;
                }
            }
        }

        var coverRatio = cover / (double)total;
        var waterRatio = water / (double)total;
        var buildingRatio = building / (double)total;
        var openRatio = open / (double)total;

        return new MapInfo
        {
            CoverRatio = coverRatio,
            WaterRatio = waterRatio,
            BuildingRatio = buildingRatio,
            OpenRatio = openRatio,
            IsOpen = coverRatio < 0.16 && waterRatio < 0.18,
            IsChokeMap = waterRatio >= 0.20,
            IsCoverHeavy = coverRatio >= 0.28,
            IsBuildingGood = buildingRatio >= 0.08
        };
    }

    private int TileScore(TileType tile, bool hasShot, MapInfo map)
    {
        return tile switch
        {
            TileType.Building => map.IsBuildingGood ? 1_550 : 1_100,
            TileType.Tree => hasShot ? -7_500 : map.IsOpen ? 250 : 850,
            TileType.Sand => 180,
            TileType.Grass => 100,
            _ => 0
        };
    }

    private int MobilityWeight(MapInfo map)
    {
        if (map.IsChokeMap)
            return 220;

        if (map.IsOpen)
            return 130;

        return 155;
    }

    private int Mobility(ITurnContext ctx, ITank me, int x, int y)
    {
        var count = 0;

        foreach (var move in Moves)
        {
            var next = AfterMove(x, y, move);

            if (!Inside(ctx, next.X, next.Y))
                continue;

            if (Tile(ctx, next.X, next.Y) == TileType.Water)
                continue;

            if (ctx.GetTanks().Any(t => t.OwnerId != me.OwnerId && t.X == next.X && t.Y == next.Y))
                continue;

            count++;
        }

        return count;
    }

    private int EdgePenalty(ITurnContext ctx, int x, int y, MapInfo map)
    {
        var edge = Math.Min(
            Math.Min(x, ctx.GetMapWidth() - 1 - x),
            Math.Min(y, ctx.GetMapHeight() - 1 - y));

        var penalty = edge switch
        {
            0 => 450,
            1 => 150,
            _ => 0
        };

        if (map.IsChokeMap && edge <= 1)
            penalty += 250;

        return penalty;
    }

    private void UpdateEnemyProfile(ITank me, ITank enemy)
    {
        var currentDistance = Distance(me.X, me.Y, enemy.X, enemy.Y);

        if (!_hasEnemyMemory)
        {
            _hasEnemyMemory = true;
            _enemyLastX = enemy.X;
            _enemyLastY = enemy.Y;
            _enemyLastDistance = currentDistance;
            return;
        }

        var stayedStill = enemy.X == _enemyLastX && enemy.Y == _enemyLastY;

        if (currentDistance < _enemyLastDistance)
            _enemyAggression += 2;
        else
            _enemyAggression--;

        if (stayedStill)
            _enemyPassivity += 2;
        else
            _enemyPassivity--;

        _enemyAggression = Clamp(_enemyAggression, 0, 10);
        _enemyPassivity = Clamp(_enemyPassivity, 0, 10);

        _enemyLastX = enemy.X;
        _enemyLastY = enemy.Y;
        _enemyLastDistance = currentDistance;
    }

    private bool CanMoveTo(ITurnContext ctx, int x, int y)
    {
        return Inside(ctx, x, y) &&
               Tile(ctx, x, y) != TileType.Water &&
               !ctx.GetTanks().Any(t => t.X == x && t.Y == y);
    }

    private static int DamageAt(ITurnContext ctx, int x, int y)
    {
        return Tile(ctx, x, y) switch
        {
            TileType.Tree => 25,
            TileType.Building => 50,
            _ => 75
        };
    }

    private static TileType Tile(ITurnContext ctx, int x, int y)
    {
        return ctx.GetTile(x, y).TileType;
    }

    private static bool Inside(ITurnContext ctx, int x, int y)
    {
        return x >= 0 &&
               y >= 0 &&
               x < ctx.GetMapWidth() &&
               y < ctx.GetMapHeight();
    }

    private static Position AfterMove(int x, int y, Direction direction)
    {
        return direction switch
        {
            Direction.North => new Position(x, y + 1),
            Direction.South => new Position(x, y - 1),
            Direction.East => new Position(x - 1, y),
            Direction.West => new Position(x + 1, y),
            _ => new Position(x, y)
        };
    }

    private static TurretDirection AimExactly(int fromX, int fromY, int toX, int toY)
    {
        var result = (TurretDirection)0;

        if (toY > fromY) result |= TurretDirection.North;
        if (toY < fromY) result |= TurretDirection.South;
        if (toX > fromX) result |= TurretDirection.West;
        if (toX < fromX) result |= TurretDirection.East;

        return result;
    }

    private static TurretDirection AimApproximately(int fromX, int fromY, int toX, int toY)
    {
        var dx = toX - fromX;
        var dy = toY - fromY;

        var horizontal = dx switch
        {
            > 0 => TurretDirection.West,
            < 0 => TurretDirection.East,
            _ => (TurretDirection)0
        };

        var vertical = dy switch
        {
            > 0 => TurretDirection.North,
            < 0 => TurretDirection.South,
            _ => (TurretDirection)0
        };

        var absDx = Math.Abs(dx);
        var absDy = Math.Abs(dy);

        if (absDx == 0) return vertical;
        if (absDy == 0) return horizontal;

        if (absDx >= absDy * 2) return horizontal;
        if (absDy >= absDx * 2) return vertical;

        return horizontal | vertical;
    }

    private static bool IsOnShotLine(int x1, int y1, int x2, int y2)
    {
        var dx = Math.Abs(x1 - x2);
        var dy = Math.Abs(y1 - y2);

        return dx == 0 || dy == 0 || dx == dy;
    }

    private static bool IsReverse(Direction previous, Direction current)
    {
        return previous switch
        {
            Direction.North => current == Direction.South,
            Direction.South => current == Direction.North,
            Direction.East => current == Direction.West,
            Direction.West => current == Direction.East,
            _ => false
        };
    }

    private static int Distance(int x1, int y1, int x2, int y2)
    {
        return Math.Abs(x1 - x2) + Math.Abs(y1 - y2);
    }

    private static int Clamp(int value, int min, int max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }

    private sealed class Candidate
    {
        public Direction? Move { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Score { get; set; }
        public ITank Target { get; set; }
    }

    private sealed class Position
    {
        public Position(int x, int y)
        {
            X = x;
            Y = y;
        }

        public int X { get; }
        public int Y { get; }
    }

    private sealed class Node
    {
        public Node(int x, int y, int depth)
        {
            X = x;
            Y = y;
            Depth = depth;
        }

        public int X { get; }
        public int Y { get; }
        public int Depth { get; }
    }

    private sealed class Threat
    {
        public int Damage { get; set; }
        public int Count { get; set; }
    }

    private sealed class MapInfo
    {
        public double CoverRatio { get; set; }
        public double WaterRatio { get; set; }
        public double BuildingRatio { get; set; }
        public double OpenRatio { get; set; }

        public bool IsOpen { get; set; }
        public bool IsChokeMap { get; set; }
        public bool IsCoverHeavy { get; set; }
        public bool IsBuildingGood { get; set; }
    }
}