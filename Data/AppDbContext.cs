using EstacionamentoAPI.Models;
using Microsoft.EntityFrameworkCore;

namespace EstacionamentoAPI.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public DbSet<Veiculo> Veiculos => Set<Veiculo>();
    public DbSet<Cliente> Clientes => Set<Cliente>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
       modelBuilder.Entity<Veiculo>()
    .HasIndex(v => v.Placa);

        modelBuilder.Entity<Veiculo>()
            .Property(v => v.ValorPago)
            .HasPrecision(10, 2);
        modelBuilder.Entity<Veiculo>()
            .HasOne(v => v.Cliente)
            .WithMany(c => c.Veiculos)
            .HasForeignKey(v => v.ClienteId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}