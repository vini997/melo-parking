namespace EstacionamentoAPI.Models;

public class Veiculo
{
    public int Id { get; set; }
    public string Placa { get; set; } = string.Empty;
    public string Marca { get; set; } = string.Empty;
    public string Modelo { get; set; } = string.Empty;
    public string Cor { get; set; } = string.Empty;
    public DateTime DataEntrada { get; set; } = DateTime.UtcNow;
    public DateTime? DataSaida { get; set; }
    public decimal? ValorPago { get; set; }
    public bool Ativo { get; set; } = true;
    public int? ClienteId { get; set; }
    public Cliente? Cliente { get; set; }
    public string? FormaPagamento { get; set; }
}