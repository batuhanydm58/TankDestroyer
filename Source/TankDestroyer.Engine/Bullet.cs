using System.Threading;
using TankDestroyer.API;

namespace TankDestroyer.Engine;

public class Bullet : IBullet
{
    private static int _globalId = 0;

    public Bullet(int ownerId)
    {
        OwnerId = ownerId;
        Id = (uint)Interlocked.Increment(ref _globalId);
    }

    private Bullet()
    {
    }

    public uint Id { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int OwnerId { get; set; }
    public TurretDirection Direction { get; set; }
    public int StartingY { get; set; }
    public int StartingX { get; set; }
    public int EndedAtX { get; set; }
    public int EndedAtY { get; set; }
    public bool Explode { get; set; }
    public bool Destroyed { get; set; }

    public Bullet Clone()
    {
        return new Bullet()
        {
            Id = Id,
            X = X,
            Y = Y,
            OwnerId = OwnerId,
            Direction = Direction,
            StartingY = StartingY,
            StartingX = StartingX,
            EndedAtX = EndedAtX,
            EndedAtY = EndedAtY,
            Explode = Explode,
            Destroyed = Destroyed
        };
    }
}