namespace Aura.Core.Entities;

public class Tenant : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;

    public ICollection<User> Users { get; set; } = new List<User>();
    public ICollection<CloudAccount> CloudAccounts { get; set; } = new List<CloudAccount>();
    public ICollection<Essence> Essences { get; set; } = new List<Essence>();
    public ICollection<Deployment> Deployments { get; set; } = new List<Deployment>();
}
