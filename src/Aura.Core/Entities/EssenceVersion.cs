namespace Aura.Core.Entities;

public class EssenceVersion : BaseEntity
{
    public Guid EssenceId { get; set; }
    public Essence Essence { get; set; } = null!;
    public int VersionNumber { get; set; }
    public string EssenceJson { get; set; } = "{}";
    public Guid ChangedByUserId { get; set; }
}
