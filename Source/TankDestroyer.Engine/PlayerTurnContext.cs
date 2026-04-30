using TankDestroyer.API;

namespace TankDestroyer.Engine;

public class PlayerTurnContext : ITurnContext
{
    private readonly PlayerBot _player;
    private readonly Game _game;
    private readonly List<TankAction> _turnActions;

    public PlayerTurnContext(PlayerBot player, Game game, List<TankAction> turnActions)
    {
        _player = player;
        Tank = game.Tanks.Single(c => c.OwnerId == player.Id);
        _game = game;
        _turnActions = turnActions;
        World = game.World;
    }

    public IWorld World { get; set; }
    public ITile GetTile(int y, int x) => World.GetTile(y, x);
    public int GetMapWidth() => World.Width;

    public int GetMapHeight()=> World.Height;

    public ITank[] GetTanks() => _game.Tanks.ToArray<ITank>();

    public IBullet[] GetBullets() => _game.Bullets
        .Where(c => c.Destroyed == false)
        .ToArray<IBullet>();

    public ITank Tank { get; set; }

    public void MoveTank(Direction direction)
    {
        _turnActions.Add(new MoveTankAction(_player.Id, direction));
    }

    public void RotateTurret(TurretDirection direction)
    {
        _turnActions.Add(new TurnTurretAction(_player.Id, direction));
    }

    public void Fire()
    {
        _turnActions.Add(new FireAction(_player.Id));
    }
}