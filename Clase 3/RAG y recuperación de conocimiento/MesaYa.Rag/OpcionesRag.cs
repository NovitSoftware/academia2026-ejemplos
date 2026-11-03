namespace MesaYa.Rag;

/// <summary>Sección "Rag" de appsettings.json. Las rutas son relativas al directorio del proyecto.</summary>
public sealed class OpcionesRag
{
    public string Corpus { get; set; } = "../corpus";
    public string Sql { get; set; } = "../db";
    public string OllamaUrl { get; set; } = "http://localhost:11434";
    public string ModeloEmbeddings { get; set; } = "bge-m3";
    public int Dimensiones { get; set; } = 1024;
    public int LoteEmbeddings { get; set; } = 32;
    public string Evaluacion { get; set; } = "../evaluacion/preguntas.json";
    public string ModeloChat { get; set; } = "qwen3.5:9b";
    public double Temperatura { get; set; } = 0.2;
    public int VentanaContexto { get; set; } = 8192;
    public string ModeloReescritura { get; set; } = "qwen3.5:2b";
    public string RerankerUrl { get; set; } = "http://localhost:7997";
    public string ModeloReranker { get; set; } = "BAAI/bge-reranker-v2-m3";
    public string Guion { get; set; } = "../guion";
}
