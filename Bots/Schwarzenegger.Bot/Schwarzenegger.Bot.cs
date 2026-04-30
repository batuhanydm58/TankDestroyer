using TankDestroyer.API;

namespace Schwarzenegger.Bot;

[Bot("SchwarzeneggerBot", "Batuhan", "#682860")]
public class SchwarzeneggerBot : IPlayerBot
{
    private readonly Random _random = new();

    private int _turnNumber;
    private Direction? _lastMove;
    private bool _peekLeftNext = true;

    public void DoTurn(ITurnContext ctx)
    {
        _turnNumber++;

        var me = ctx.Tank;

        var enemies = ctx.GetTanks()
            .Where(t => !t.Destroyed && t.OwnerId != me.OwnerId)
            .ToArray();

        if (enemies.Length == 0)
            return;

        var target = PickTarget(me, enemies);

        var plannedX = me.X;
        var plannedY = me.Y;

        var move = ChooseMove(ctx, me, target, enemies);
        if (move.HasValue)
        {
            ctx.MoveTank(move.Value);
            _lastMove = move.Value;

            var next = GetPositionAfterMove(me.X, me.Y, move.Value);
            plannedX = next.X;
            plannedY = next.Y;
        }

        var finalTarget = PickTargetFromPosition(plannedX, plannedY, enemies);

        ctx.RotateTurret(DirectionToTarget(plannedX, plannedY, finalTarget.X, finalTarget.Y));
        ctx.Fire();
    }

    private Direction? ChooseMove(ITurnContext ctx, ITank me, ITank target, ITank[] enemies)
    {
        var legalMoves = GetLegalMoves(ctx, me);

        if (legalMoves.Count == 0)
            return null;

        var distance = Distance(me.X, me.Y, target.X, target.Y);

        if (distance <= 6)
            return ChooseJiggleAttackMove(ctx, me, target, legalMoves, enemies);

        return ChooseRushCoverMove(ctx, target, legalMoves, enemies);
    }

    private Direction ChooseRushCoverMove(
        ITurnContext ctx,
        ITank target,
        List<MoveOption> legalMoves,
        ITank[] enemies)
    {
        return legalMoves
            .OrderByDescending(m => RushScore(ctx, m.X, m.Y, target, enemies))
            .First()
            .Direction;
    }

    private int RushScore(ITurnContext ctx, int x, int y, ITank target, ITank[] enemies)
    {
        var score = 0;

        score -= Distance(x, y, target.X, target.Y) * 100;

        score += ctx.GetTile(x, y).TileType switch
        {
            TileType.Building => 45,
            TileType.Tree => 25,
            TileType.Sand => 8,
            TileType.Grass => 4,
            _ => 0
        };

        if (IsStraightLine(x, y, target.X, target.Y))
            score += 90;

        if (IsDiagonalLine(x, y, target.X, target.Y))
            score += 55;

        if (enemies.Length == 2)
            score -= EnemyDangerScore(ctx, x, y, enemies) * 35;

        score += _random.Next(0, 10);

        return score;
    }

    private Direction ChooseJiggleAttackMove(
        ITurnContext ctx,
        ITank me,
        ITank target,
        List<MoveOption> legalMoves,
        ITank[] enemies)
    {
        var best = legalMoves
            .OrderByDescending(m => JiggleAttackScore(ctx, me, m.X, m.Y, target, enemies))
            .First();

        _peekLeftNext = !_peekLeftNext;

        return best.Direction;
    }

    private int JiggleAttackScore(ITurnContext ctx, ITank me, int x, int y, ITank target, ITank[] enemies)
    {
        var score = 0;

        var distance = Distance(x, y, target.X, target.Y);
        var oneVOne = enemies.Length == 1;

        score -= distance * (oneVOne ? 45 : 60);

        if (IsStraightLine(x, y, target.X, target.Y))
            score += distance <= 6 ? 300 : 120;

        if (IsDiagonalLine(x, y, target.X, target.Y))
            score += distance <= 4 ? 220 : 80;

        score += ctx.GetTile(x, y).TileType switch
        {
            TileType.Building => oneVOne ? 100 : 80,
            TileType.Tree => oneVOne ? 15 : 5,
            TileType.Sand => 8,
            TileType.Grass => 4,
            _ => 0
        };

        if (target.Health <= 75)
            score += 120;

        if (target.Health <= 50)
            score += 160;

        if (target.Health <= 25)
            score += 220;

        var currentMove = DirectionFromPositions(me.X, me.Y, x, y);

        if (_lastMove.HasValue && WouldReverse(_lastMove.Value, currentMove))
            score += oneVOne ? 160 : 45;

        if (oneVOne)
        {
            if (IsSideStep(me, x, y, target))
                score += 120;

            if (IsStraightLine(me.X, me.Y, target.X, target.Y) &&
                IsStraightLine(x, y, target.X, target.Y))
                score -= 80;

            if (!IsStraightLine(me.X, me.Y, target.X, target.Y) &&
                IsStraightLine(x, y, target.X, target.Y))
                score += 130;
        }

        if (enemies.Length == 2)
        {
            score -= EnemyDangerScore(ctx, x, y, enemies) * 85;

            score += ctx.GetTile(x, y).TileType switch
            {
                TileType.Building => 80,
                TileType.Tree => 35,
                _ => 0
            };
        }

        if (_peekLeftNext && x < me.X)
            score += 20;

        if (!_peekLeftNext && x > me.X)
            score += 20;

        score += _random.Next(0, oneVOne ? 28 : 18);

        return score;
    }

    private bool IsSideStep(ITank me, int newX, int newY, ITank target)
    {
        var currentDx = Math.Abs(target.X - me.X);
        var currentDy = Math.Abs(target.Y - me.Y);

        var newDx = Math.Abs(target.X - newX);
        var newDy = Math.Abs(target.Y - newY);

        if (currentDx >= currentDy)
        {
            return newY != me.Y;
        }

        return newX != me.X;
    }

    private ITank PickTarget(ITank me, ITank[] enemies)
    {
        return enemies
            .OrderBy(t => t.Health)
            .ThenBy(t => Distance(me.X, me.Y, t.X, t.Y))
            .First();
    }

    private ITank PickTargetFromPosition(int x, int y, ITank[] enemies)
    {
        return enemies
            .OrderByDescending(t => ShotValueFromPosition(x, y, t))
            .ThenBy(t => t.Health)
            .ThenBy(t => Distance(x, y, t.X, t.Y))
            .First();
    }

    private int ShotValueFromPosition(int x, int y, ITank target)
    {
        var distance = Distance(x, y, target.X, target.Y);
        var score = 0;

        if (IsStraightLine(x, y, target.X, target.Y) && distance <= 6)
            score += 500;

        if (IsDiagonalLine(x, y, target.X, target.Y) && distance <= 4)
            score += 350;

        if (target.Health <= 75)
            score += 100;

        if (target.Health <= 50)
            score += 150;

        if (target.Health <= 25)
            score += 250;

        return score;
    }

    private int EnemyDangerScore(ITurnContext ctx, int x, int y, ITank[] enemies)
    {
        var danger = 0;

        foreach (var enemy in enemies)
        {
            var distance = Distance(x, y, enemy.X, enemy.Y);

            if (IsStraightLine(x, y, enemy.X, enemy.Y) && distance <= 6)
                danger += 3;

            if (IsDiagonalLine(x, y, enemy.X, enemy.Y) && distance <= 4)
                danger += 1;
        }

        return danger;
    }

    private List<MoveOption> GetLegalMoves(ITurnContext ctx, ITank me)
    {
        var moves = new List<MoveOption>
        {
            new(Direction.North, me.X, me.Y + 1),
            new(Direction.South, me.X, me.Y - 1),
            new(Direction.East,  me.X - 1, me.Y),
            new(Direction.West,  me.X + 1, me.Y)
        };

        return moves
            .Where(m => InsideMap(ctx, m.X, m.Y))
            .Where(m => ctx.GetTile(m.X, m.Y).TileType != TileType.Water)
            .Where(m => !ctx.GetTanks().Any(t => t.X == m.X && t.Y == m.Y))
            .ToList();
    }

    private TurretDirection DirectionToTarget(int fromX, int fromY, int toX, int toY)
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

        return horizontal | vertical;
    }

    private Position GetPositionAfterMove(int x, int y, Direction direction)
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

    private bool WouldReverse(Direction previous, Direction current)
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

    private Direction DirectionFromPositions(int fromX, int fromY, int toX, int toY)
    {
        if (toY > fromY) return Direction.North;
        if (toY < fromY) return Direction.South;
        if (toX < fromX) return Direction.East;
        return Direction.West;
    }

    private bool InsideMap(ITurnContext ctx, int x, int y)
    {
        return x >= 0 &&
               y >= 0 &&
               x < ctx.GetMapWidth() &&
               y < ctx.GetMapHeight();
    }

    private bool IsStraightLine(int x1, int y1, int x2, int y2)
    {
        return x1 == x2 || y1 == y2;
    }

    private bool IsDiagonalLine(int x1, int y1, int x2, int y2)
    {
        return Math.Abs(x1 - x2) == Math.Abs(y1 - y2);
    }

    private int Distance(int x1, int y1, int x2, int y2)
    {
        return Math.Abs(x1 - x2) + Math.Abs(y1 - y2);
    }

    private class MoveOption
    {
        public MoveOption(Direction direction, int x, int y)
        {
            Direction = direction;
            X = x;
            Y = y;
        }

        public Direction Direction { get; }
        public int X { get; }
        public int Y { get; }
    }

    private class Position
    {
        public Position(int x, int y)
        {
            X = x;
            Y = y;
        }

        public int X { get; }
        public int Y { get; }
    }
}
