#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MesaPartesEsSalud;

// ======== Enumeraciones.cs ========
/// <summary>
/// Estados del ciclo de vida de un expediente (RF-03).
/// El orden de los valores es el orden en que debe avanzar el trámite.
/// </summary>
public enum Estado
{
    Recibido,
    Derivado,
    Atendido,
    Archivado
}

/// <summary>
/// Tipos de trámite que atiende la mesa de partes (RF-01).
/// </summary>
public enum TipoTramite
{
    Subsidio,
    Reclamo,
    HistoriaClinica
}

// ======== Asegurado.cs ========
/// <summary>
/// Persona asegurada que presenta un trámite.
/// Ley N.° 29733 (RD-02, RF-02, RF-08): el DNI en texto plano solo existe dentro del
/// constructor. Después se conserva únicamente su versión enmascarada y una huella HMAC-SHA256.
/// </summary>
public class Asegurado
{
    private readonly string nombreCompleto;
    private readonly string dniEnmascarado;
    private readonly string dniHash;

    /// <summary>
    /// Crea un asegurado validando sus datos.
    /// </summary>
    /// <exception cref="ArgumentException">Si el DNI no tiene 8 dígitos o el nombre está vacío.</exception>
    public Asegurado(string dniPlano, string nombre)
    {
        if (!ValidarDni(dniPlano))
            throw new ArgumentException("El DNI debe tener exactamente 8 dígitos numéricos.");

        if (string.IsNullOrWhiteSpace(nombre))
            throw new ArgumentException("El nombre del asegurado no puede estar vacío.");

        nombreCompleto = nombre.Trim();
        dniEnmascarado = "****" + dniPlano.Substring(4, 4); // formato ****5678
        dniHash = GenerarHuella(dniPlano);                   // el DNI plano no se guarda
    }

    // Constructor privado: solo se usa para reconstruir un asegurado desde el archivo JSON,
    // donde ya no existe el DNI en texto plano.
    private Asegurado(string nombreCompleto, string dniEnmascarado, string dniHash)
    {
        this.nombreCompleto = nombreCompleto;
        this.dniEnmascarado = dniEnmascarado;
        this.dniHash = dniHash;
    }

    /// <summary>Nombre completo del asegurado (solo lectura).</summary>
    public string NombreCompleto => nombreCompleto;

    /// <summary>DNI parcialmente oculto, por ejemplo ****5678 (solo lectura).</summary>
    public string DniEnmascarado => dniEnmascarado;

    // Solo lo usa RepositorioJson (mismo proyecto) para guardar la huella, nunca el DNI.
    internal string DniHash => dniHash;

    internal static Asegurado Restaurar(string nombreCompleto, string dniEnmascarado, string dniHash)
    {
        return new Asegurado(nombreCompleto, dniEnmascarado, dniHash);
    }

    /// <summary>
    /// Indica si el texto es un DNI válido: exactamente 8 caracteres entre '0' y '9'.
    /// (char.IsAsciiDigit no acepta dígitos de otros alfabetos, a diferencia de char.IsDigit.)
    /// </summary>
    public static bool ValidarDni(string? dni)
    {
        return dni is not null && dni.Length == 8 && dni.All(char.IsAsciiDigit);
    }

    /// <summary>
    /// Comprueba si un DNI corresponde a este asegurado comparando su huella,
    /// sin necesidad de haber guardado el DNI.
    /// </summary>
    public bool CoincideConDni(string? dniPlano)
    {
        return dniPlano is not null
            && ValidarDni(dniPlano)
            && string.Equals(GenerarHuella(dniPlano), dniHash, StringComparison.Ordinal);
    }

    // HMAC-SHA256 con clave secreta. Un SHA256 simple de un DNI (solo 100 millones de
    // combinaciones) se podría descubrir por fuerza bruta; con clave, no.
    // La clave se lee de la variable de entorno MESAPARTES_CLAVE; el valor por defecto
    // solo sirve para el prototipo académico.
    private static string GenerarHuella(string dni)
    {
        string clave = Environment.GetEnvironmentVariable("MESAPARTES_CLAVE") ?? "clave-de-desarrollo";
        byte[] huella = HMACSHA256.HashData(Encoding.UTF8.GetBytes(clave), Encoding.UTF8.GetBytes(dni));
        return Convert.ToBase64String(huella);
    }
}

// ======== MovimientoHistorial.cs ========
/// <summary>
/// Registro de auditoría de un cambio de estado de un expediente (RF-04).
/// Es inmutable: una vez creado, no se puede modificar.
/// </summary>
public class MovimientoHistorial
{
    public Estado Estado { get; }
    public DateTime Fecha { get; }
    public string Responsable { get; }
    public string Observacion { get; }

    public MovimientoHistorial(Estado estado, DateTime fecha, string responsable, string observacion)
    {
        Estado = estado;
        Fecha = fecha;
        Responsable = responsable;
        Observacion = observacion;
    }
}

// ======== Expediente.cs ========
/// <summary>
/// Trámite presentado por un asegurado. Es una clase abstracta: cada tipo de trámite
/// hereda de ella y define su propio plazo (herencia y polimorfismo).
/// Contiene el historial de movimientos (composición: el historial no existe fuera del expediente).
/// </summary>
public abstract class Expediente
{
    private readonly List<MovimientoHistorial> historial = new();

    /// <summary>Código correlativo, por ejemplo ESS-001.</summary>
    public string Codigo { get; }

    public Asegurado Asegurado { get; }
    public DateTime FechaRegistro { get; private set; }
    public Estado EstadoActual { get; private set; }

    /// <summary>Historial de solo lectura: solo cambia mediante <see cref="CambiarEstado"/>.</summary>
    public IReadOnlyList<MovimientoHistorial> Historial => historial.AsReadOnly();

    /// <summary>Plazo de atención en días calendario (RD-07). Cada subclase define el suyo.</summary>
    public abstract int PlazoDias { get; }

    public abstract TipoTramite Tipo { get; }

    /// <summary>Dato propio del trámite (días de incapacidad, motivo o número de historia).</summary>
    public abstract string Detalle { get; }

    /// <summary>Un expediente está pendiente mientras esté Recibido o Derivado.</summary>
    public bool EstaPendiente => EstadoActual == Estado.Recibido || EstadoActual == Estado.Derivado;

    protected Expediente(string codigo, Asegurado asegurado, DateTime fechaRegistro)
    {
        if (string.IsNullOrWhiteSpace(codigo))
            throw new ArgumentException("El código del expediente no puede estar vacío.");

        Codigo = codigo;
        Asegurado = asegurado ?? throw new ArgumentNullException(nameof(asegurado));
        FechaRegistro = fechaRegistro;
        EstadoActual = Estado.Recibido;
        historial.Add(new MovimientoHistorial(Estado.Recibido, fechaRegistro, "Mesa de Partes", "Expediente registrado."));
    }

    /// <summary>Fecha límite de atención: fecha de registro más el plazo del tipo de trámite.</summary>
    public DateTime FechaLimite() => FechaRegistro.AddDays(PlazoDias);

    /// <summary>
    /// Cambia el estado del expediente. Los estados solo avanzan de uno en uno
    /// (Recibido, Derivado, Atendido, Archivado); un salto se rechaza y el estado se conserva (RF-03, CE-01).
    /// </summary>
    /// <exception cref="InvalidOperationException">Si el expediente está archivado o se salta un estado.</exception>
    public void CambiarEstado(Estado nuevoEstado, string responsable, DateTime fecha, string observacion = "")
    {
        if (EstadoActual == Estado.Archivado)
            throw new InvalidOperationException("No se puede modificar un expediente ya archivado.");

        if ((int)nuevoEstado != (int)EstadoActual + 1)
            throw new InvalidOperationException($"Transición no permitida: de {EstadoActual} a {nuevoEstado}.");

        EstadoActual = nuevoEstado;
        historial.Add(new MovimientoHistorial(nuevoEstado, fecha, responsable, observacion));
    }

    /// <summary>Un expediente está vencido si sigue pendiente y ya pasó su fecha límite (RF-06).</summary>
    public bool EstaVencido(DateTime hoy) => EstaPendiente && hoy > FechaLimite();

    /// <summary>Días que tardó la atención, o null si todavía no fue atendido.</summary>
    public int? DiasDeAtencion()
    {
        MovimientoHistorial? atencion = historial.FirstOrDefault(m => m.Estado == Estado.Atendido);
        if (atencion is null)
            return null;

        return (int)(atencion.Fecha - FechaRegistro).TotalDays;
    }

    // Solo lo usa RepositorioJson para reconstruir un expediente guardado.
    internal void Restaurar(DateTime fechaRegistro, Estado estadoActual, IEnumerable<MovimientoHistorial> movimientos)
    {
        FechaRegistro = fechaRegistro;
        EstadoActual = estadoActual;
        historial.Clear();
        historial.AddRange(movimientos);
    }
}

// ======== ExpedientesConcretos.cs ========
/// <summary>Subsidio por incapacidad. Plazo: 15 días calendario (RD-07).</summary>
public class ExpedienteSubsidio : Expediente
{
    public int DiasIncapacidad { get; }

    public override int PlazoDias => 15;
    public override TipoTramite Tipo => TipoTramite.Subsidio;
    public override string Detalle => DiasIncapacidad.ToString();

    public ExpedienteSubsidio(string codigo, Asegurado asegurado, DateTime fechaRegistro, int diasIncapacidad)
        : base(codigo, asegurado, fechaRegistro)
    {
        if (diasIncapacidad <= 0)
            throw new ArgumentException("Los días de incapacidad deben ser mayores que cero.");

        DiasIncapacidad = diasIncapacidad;
    }
}

/// <summary>Reclamo por demora o mala atención. Plazo: 10 días calendario (RD-07).</summary>
public class ExpedienteReclamo : Expediente
{
    public string MotivoReclamo { get; }

    public override int PlazoDias => 10;
    public override TipoTramite Tipo => TipoTramite.Reclamo;
    public override string Detalle => MotivoReclamo;

    public ExpedienteReclamo(string codigo, Asegurado asegurado, DateTime fechaRegistro, string motivo)
        : base(codigo, asegurado, fechaRegistro)
    {
        if (string.IsNullOrWhiteSpace(motivo))
            throw new ArgumentException("El motivo del reclamo no puede estar vacío.");

        MotivoReclamo = motivo.Trim();
    }
}

/// <summary>Solicitud de copia de historia clínica. Plazo: 5 días calendario (RD-07).</summary>
public class ExpedienteHistoriaClinica : Expediente
{
    public string NroHistoria { get; }

    public override int PlazoDias => 5;
    public override TipoTramite Tipo => TipoTramite.HistoriaClinica;
    public override string Detalle => NroHistoria;

    public ExpedienteHistoriaClinica(string codigo, Asegurado asegurado, DateTime fechaRegistro, string nroHistoria)
        : base(codigo, asegurado, fechaRegistro)
    {
        if (string.IsNullOrWhiteSpace(nroHistoria))
            throw new ArgumentException("El número de historia clínica no puede estar vacío.");

        NroHistoria = nroHistoria.Trim();
    }
}

// ======== IGestionTramite.cs ========
/// <summary>
/// Contrato de las operaciones principales de la mesa de partes (RF-01, RF-05, RF-06).
/// Permite reemplazar la implementación (por ejemplo, por una con base de datos)
/// sin cambiar a quien la usa (principio de inversión de dependencias).
/// </summary>
public interface IGestionTramite
{
    Expediente RegistrarExpediente(TipoTramite tipo, string dni, string nombre, string detalle, DateTime fecha);

    Expediente? BuscarPorCodigo(string codigo);

    List<Expediente> VerificarVencimientos(DateTime hoy);
}

// ======== PlazoVencidoEventArgs.cs ========
/// <summary>
/// Datos que se envían a quien escucha el evento de plazo vencido (patrón Observer, RF-06).
/// </summary>
public class PlazoVencidoEventArgs : EventArgs
{
    public Expediente Expediente { get; }
    public int DiasDeRetraso { get; }

    public PlazoVencidoEventArgs(Expediente expediente, int diasDeRetraso)
    {
        Expediente = expediente;
        DiasDeRetraso = diasDeRetraso;
    }
}

// ======== ExpedienteFactory.cs ========
/// <summary>
/// Patrón Factory (fábrica simple): decide qué subclase de Expediente crear según el tipo
/// de trámite, así el gestor no necesita conocer las clases concretas (RF-01).
/// </summary>
public static class ExpedienteFactory
{
    /// <summary>
    /// Crea el expediente que corresponde al tipo de trámite.
    /// El significado de <paramref name="detalle"/> depende del tipo: días de incapacidad
    /// (subsidio), motivo (reclamo) o número de historia (historia clínica).
    /// </summary>
    /// <exception cref="ArgumentException">Si el tipo no existe o el detalle no es válido.</exception>
    public static Expediente Crear(TipoTramite tipo, string codigo, Asegurado asegurado, DateTime fecha, string detalle)
    {
        return tipo switch
        {
            TipoTramite.Subsidio => new ExpedienteSubsidio(codigo, asegurado, fecha, LeerDiasIncapacidad(detalle)),
            TipoTramite.Reclamo => new ExpedienteReclamo(codigo, asegurado, fecha, detalle),
            TipoTramite.HistoriaClinica => new ExpedienteHistoriaClinica(codigo, asegurado, fecha, detalle),
            _ => throw new ArgumentException("Tipo de trámite no válido.")
        };
    }

    private static int LeerDiasIncapacidad(string detalle)
    {
        if (!int.TryParse(detalle, out int dias))
            throw new ArgumentException("Para un subsidio, el detalle debe ser el número de días de incapacidad.");

        return dias;
    }
}

// ======== GestorMesaPartes.cs ========
/// <summary>
/// Gestor central de la mesa de partes.
/// Patrón Singleton: una sola instancia garantiza una sola lista de expedientes y un solo contador,
/// así dos gestores no pueden asignar el mismo código (RF-01, RF-05).
/// Patrón Observer: publica el evento <see cref="AlertaPlazoVencido"/> (RF-06).
/// Agrega los expedientes que administra (composición).
/// </summary>
public sealed class GestorMesaPartes : IGestionTramite
{
    private static readonly Lazy<GestorMesaPartes> instancia = new(() => new GestorMesaPartes());

    /// <summary>Única instancia del gestor (se crea la primera vez que se usa, de forma segura entre hilos).</summary>
    public static GestorMesaPartes Instancia => instancia.Value;

    private readonly List<Expediente> expedientes = new();
    private int correlativoActual = 1;

    /// <summary>Se dispara por cada expediente pendiente que superó su plazo.</summary>
    public event EventHandler<PlazoVencidoEventArgs>? AlertaPlazoVencido;

    private GestorMesaPartes()
    {
    }

    /// <summary>
    /// Registra un expediente nuevo con el siguiente código correlativo (ESS-001, ESS-002, ...).
    /// Si los datos no son válidos, la excepción sube a quien llama y el correlativo no avanza.
    /// </summary>
    /// <exception cref="ArgumentException">Si el DNI, el nombre o el detalle no son válidos.</exception>
    public Expediente RegistrarExpediente(TipoTramite tipo, string dni, string nombre, string detalle, DateTime fecha)
    {
        string codigo = $"ESS-{correlativoActual:D3}";
        Asegurado asegurado = new Asegurado(dni, nombre);
        Expediente nuevo = ExpedienteFactory.Crear(tipo, codigo, asegurado, fecha, detalle);

        expedientes.Add(nuevo);
        correlativoActual++;
        return nuevo;
    }

    /// <summary>Busca un expediente por su código (sin distinguir mayúsculas). Devuelve null si no existe.</summary>
    public Expediente? BuscarPorCodigo(string codigo)
    {
        return expedientes.FirstOrDefault(e => e.Codigo.Equals(codigo, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Deriva un expediente a un área (Recibido a Derivado).</summary>
    public void Derivar(string codigo, string area, DateTime fecha)
    {
        ObtenerExistente(codigo).CambiarEstado(Estado.Derivado, area, fecha, $"Derivado al área: {area}.");
    }

    /// <summary>Marca un expediente como atendido (Derivado a Atendido).</summary>
    public void Atender(string codigo, string responsable, DateTime fecha)
    {
        ObtenerExistente(codigo).CambiarEstado(Estado.Atendido, responsable, fecha, "Expediente atendido.");
    }

    /// <summary>Archiva un expediente atendido (Atendido a Archivado).</summary>
    public void Archivar(string codigo, string responsable, DateTime fecha)
    {
        ObtenerExistente(codigo).CambiarEstado(Estado.Archivado, responsable, fecha, "Expediente archivado.");
    }

    /// <summary>
    /// Revisa los plazos a una fecha dada. Dispara <see cref="AlertaPlazoVencido"/> solo por los
    /// expedientes pendientes fuera de plazo (CE-04) y devuelve esa lista.
    /// </summary>
    public List<Expediente> VerificarVencimientos(DateTime hoy)
    {
        List<Expediente> vencidos = ReporteExpedientes.Vencidos(expedientes, hoy);

        foreach (Expediente expediente in vencidos)
        {
            int diasDeRetraso = (int)(hoy - expediente.FechaLimite()).TotalDays;
            AlertaPlazoVencido?.Invoke(this, new PlazoVencidoEventArgs(expediente, diasDeRetraso));
        }

        return vencidos;
    }

    /// <summary>Devuelve una vista de solo lectura: nadie puede modificar la lista interna.</summary>
    public IReadOnlyList<Expediente> ObtenerTodos() => expedientes.AsReadOnly();

    /// <summary>Deja el gestor como nuevo (sin expedientes, correlativo en 1 y sin suscriptores). Útil en las pruebas.</summary>
    public void Reiniciar()
    {
        expedientes.Clear();
        correlativoActual = 1;
        AlertaPlazoVencido = null;
    }

    private Expediente ObtenerExistente(string codigo)
    {
        return BuscarPorCodigo(codigo)
            ?? throw new KeyNotFoundException($"No existe el expediente {codigo}.");
    }
}

// ======== ReporteExpedientes.cs ========
/// <summary>
/// Reportes sobre la colección de expedientes (RF-07).
/// Usa funciones de orden superior de LINQ con expresiones lambda: Where (filter),
/// Select (map) y Aggregate (reduce). Ninguna modifica la colección original.
/// Está separada del gestor por el principio de responsabilidad única.
/// </summary>
public static class ReporteExpedientes
{
    /// <summary>filter: expedientes que siguen en trámite (Recibido o Derivado).</summary>
    public static List<Expediente> Pendientes(IEnumerable<Expediente> lista)
    {
        return lista.Where(e => e.EstaPendiente).ToList();
    }

    /// <summary>filter: expedientes pendientes cuyo plazo ya venció a la fecha indicada.</summary>
    public static List<Expediente> Vencidos(IEnumerable<Expediente> lista, DateTime hoy)
    {
        return lista.Where(e => e.EstaVencido(hoy)).ToList();
    }

    /// <summary>Cantidad de expedientes por estado.</summary>
    public static Dictionary<Estado, int> ConteoPorEstado(IEnumerable<Expediente> lista)
    {
        return lista.GroupBy(e => e.EstadoActual)
                    .ToDictionary(grupo => grupo.Key, grupo => grupo.Count());
    }

    /// <summary>Cantidad de expedientes por tipo de trámite.</summary>
    public static Dictionary<TipoTramite, int> ConteoPorTipo(IEnumerable<Expediente> lista)
    {
        return lista.GroupBy(e => e.Tipo)
                    .ToDictionary(grupo => grupo.Key, grupo => grupo.Count());
    }

    /// <summary>
    /// Promedio de días de atención de los expedientes ya atendidos (0 si no hay ninguno).
    /// Combina map (Select), filter (Where) y reduce (Aggregate).
    /// </summary>
    public static double PromedioDiasAtencion(IEnumerable<Expediente> lista)
    {
        List<int> dias = lista.Select(e => e.DiasDeAtencion())
                              .Where(d => d.HasValue)
                              .Select(d => d.GetValueOrDefault())
                              .ToList();

        if (dias.Count == 0)
            return 0;

        int total = dias.Aggregate(0, (suma, d) => suma + d);
        return (double)total / dias.Count;
    }
}

// ======== RepositorioJson.cs ========
/// <summary>
/// Guarda y lee expedientes en un archivo JSON sin exponer el DNI completo (RF-08, RD-02, CE-02).
/// Del asegurado solo se escribe el nombre, el DNI enmascarado y la huella HMAC.
/// Está separada del gestor por el principio de responsabilidad única.
/// </summary>
public static class RepositorioJson
{
    private static readonly JsonSerializerOptions Opciones = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Guarda los expedientes (con su historial) en el archivo indicado.</summary>
    public static void Guardar(IEnumerable<Expediente> expedientes, string ruta)
    {
        List<ExpedienteDto> datos = expedientes.Select(ADto).ToList();
        File.WriteAllText(ruta, JsonSerializer.Serialize(datos, Opciones));
    }

    /// <summary>Lee los expedientes guardados en el archivo indicado.</summary>
    /// <exception cref="FileNotFoundException">Si el archivo no existe.</exception>
    public static List<Expediente> Cargar(string ruta)
    {
        string json = File.ReadAllText(ruta);
        List<ExpedienteDto> datos = JsonSerializer.Deserialize<List<ExpedienteDto>>(json, Opciones)
                                    ?? new List<ExpedienteDto>();
        return datos.Select(ADominio).ToList();
    }

    private static ExpedienteDto ADto(Expediente e)
    {
        return new ExpedienteDto
        {
            Codigo = e.Codigo,
            Tipo = e.Tipo,
            Detalle = e.Detalle,
            FechaRegistro = e.FechaRegistro,
            EstadoActual = e.EstadoActual,
            NombreCompleto = e.Asegurado.NombreCompleto,
            DniEnmascarado = e.Asegurado.DniEnmascarado,
            DniHash = e.Asegurado.DniHash,
            Historial = e.Historial.Select(m => new MovimientoDto
            {
                Estado = m.Estado,
                Fecha = m.Fecha,
                Responsable = m.Responsable,
                Observacion = m.Observacion
            }).ToList()
        };
    }

    private static Expediente ADominio(ExpedienteDto d)
    {
        Asegurado asegurado = Asegurado.Restaurar(d.NombreCompleto, d.DniEnmascarado, d.DniHash);
        Expediente expediente = ExpedienteFactory.Crear(d.Tipo, d.Codigo, asegurado, d.FechaRegistro, d.Detalle);

        IEnumerable<MovimientoHistorial> movimientos = d.Historial.Select(
            m => new MovimientoHistorial(m.Estado, m.Fecha, m.Responsable, m.Observacion));

        expediente.Restaurar(d.FechaRegistro, d.EstadoActual, movimientos);
        return expediente;
    }
}

// Objetos simples que representan cómo se escribe cada dato en el archivo JSON.
internal sealed class MovimientoDto
{
    public Estado Estado { get; set; }
    public DateTime Fecha { get; set; }
    public string Responsable { get; set; } = "";
    public string Observacion { get; set; } = "";
}

internal sealed class ExpedienteDto
{
    public string Codigo { get; set; } = "";
    public TipoTramite Tipo { get; set; }
    public string Detalle { get; set; } = "";
    public DateTime FechaRegistro { get; set; }
    public Estado EstadoActual { get; set; }
    public string NombreCompleto { get; set; } = "";
    public string DniEnmascarado { get; set; } = "";
    public string DniHash { get; set; } = "";
    public List<MovimientoDto> Historial { get; set; } = new();
}

// ======== Program.cs ========
/// <summary>
/// Demostración por consola del módulo: registro, protección del DNI, ciclo de vida,
/// manejo de errores, reportes con LINQ, evento de plazo vencido y persistencia en JSON.
/// Los datos son ficticios (RD-03).
/// </summary>
internal static class Program
{
    private static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== SISTEMA MESA DE PARTES DIGITAL - ESSALUD INDEPENDENCIA ===");

        // Fecha fija para que la demostración se repita igual en cada ejecución.
        DateTime hoy = new DateTime(2026, 11, 16);
        GestorMesaPartes gestor = GestorMesaPartes.Instancia;

        // Paradigma orientado a eventos: nos suscribimos al evento de plazo vencido (Observer).
        gestor.AlertaPlazoVencido += (sender, e) =>
        {
            Console.WriteLine($"[¡ALERTA PLAZO!] Expediente {e.Expediente.Codigo} vencido por {e.DiasDeRetraso} días.");
        };

        // 1. Registro de expedientes (RF-01) con el DNI protegido (RF-02, RD-02)
        Console.WriteLine("\n--- 1. REGISTRO DE EXPEDIENTES ---");
        gestor.RegistrarExpediente(TipoTramite.Subsidio, "45871234", "Maria Quispe", "30", hoy);
        gestor.RegistrarExpediente(TipoTramite.Reclamo, "71234567", "Juan Perez", "Demora en entrega de medicamentos", hoy);
        gestor.RegistrarExpediente(TipoTramite.HistoriaClinica, "30112233", "Rosa Flores", "HC-0457", hoy);

        foreach (Expediente e in gestor.ObtenerTodos())
        {
            Console.WriteLine($"{e.Codigo} | {e.Tipo} | {e.Asegurado.NombreCompleto} | DNI: {e.Asegurado.DniEnmascarado} | Plazo: {e.PlazoDias} días");
        }

        // 2. Verificación del DNI sin haberlo guardado (huella HMAC)
        Console.WriteLine("\n--- 2. PROTECCIÓN DE DATOS PERSONALES (Ley N.° 29733) ---");
        Expediente primero = gestor.ObtenerTodos()[0];
        Console.WriteLine($"¿45871234 es el DNI de {primero.Asegurado.NombreCompleto}? {primero.Asegurado.CoincideConDni("45871234")}");
        Console.WriteLine($"¿99999999 es el DNI de {primero.Asegurado.NombreCompleto}? {primero.Asegurado.CoincideConDni("99999999")}");

        // 3. Ciclo de vida, historial (RF-03, RF-04) y búsqueda (RF-05)
        Console.WriteLine("\n--- 3. CICLO DE VIDA, HISTORIAL Y BÚSQUEDA ---");
        gestor.Derivar("ESS-001", "Prestaciones Económicas", hoy.AddDays(1));
        gestor.Atender("ESS-001", "Prestaciones Económicas", hoy.AddDays(3));

        Expediente? encontrado = gestor.BuscarPorCodigo("ess-001");
        if (encontrado != null)
        {
            Console.WriteLine($"Historial del expediente {encontrado.Codigo}:");
            foreach (MovimientoHistorial m in encontrado.Historial)
            {
                Console.WriteLine($"  {m.Fecha:dd/MM/yyyy} | {m.Estado} | {m.Responsable} | {m.Observacion}");
            }
        }

        // 4. Manejo de errores y excepciones: cada tipo de error se captura por separado
        Console.WriteLine("\n--- 4. MANEJO DE ERRORES Y EXCEPCIONES ---");
        try
        {
            Console.WriteLine("Intentando registrar un DNI inválido (debe fallar por validación):");
            gestor.RegistrarExpediente(TipoTramite.HistoriaClinica, "123", "Carlos Ruiz", "HC-992", hoy);
        }
        catch (ArgumentException ex)
        {
            Console.WriteLine($"Excepción capturada correctamente: {ex.Message}");
        }

        try
        {
            Console.WriteLine("Intentando atender el expediente ESS-002, que aún está en estado Recibido:");
            gestor.Atender("ESS-002", "Mesa de Partes", hoy);
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine($"Excepción capturada correctamente: {ex.Message}");
        }

        // 5. Reportes con funciones de orden superior (RF-07)
        Console.WriteLine("\n--- 5. REPORTES (LINQ: Where, GroupBy, Select, Aggregate) ---");
        var todos = gestor.ObtenerTodos();
        Console.WriteLine($"Expedientes pendientes: {ReporteExpedientes.Pendientes(todos).Count}");

        Console.WriteLine("Conteo por estado:");
        foreach (var par in ReporteExpedientes.ConteoPorEstado(todos))
        {
            Console.WriteLine($"  {par.Key}: {par.Value}");
        }

        Console.WriteLine("Conteo por tipo:");
        foreach (var par in ReporteExpedientes.ConteoPorTipo(todos))
        {
            Console.WriteLine($"  {par.Key}: {par.Value}");
        }

        Console.WriteLine($"Promedio de días de atención: {ReporteExpedientes.PromedioDiasAtencion(todos):F1}");

        // 6. Evento de plazo vencido (RF-06): pasan 12 días
        Console.WriteLine("\n--- 6. VERIFICACIÓN DE PLAZOS 12 DÍAS DESPUÉS ---");
        var vencidos = gestor.VerificarVencimientos(hoy.AddDays(12));
        Console.WriteLine($"Expedientes vencidos: {vencidos.Count}");

        // 7. Persistencia en JSON sin el DNI completo (RF-08, CE-02)
        Console.WriteLine("\n--- 7. PERSISTENCIA EN JSON ---");
        string ruta = Path.Combine(Path.GetTempPath(), "expedientes_mesa_partes.json");
        RepositorioJson.Guardar(gestor.ObtenerTodos(), ruta);

        string contenido = File.ReadAllText(ruta);
        bool contieneDni = contenido.Contains("45871234") || contenido.Contains("71234567") || contenido.Contains("30112233");
        Console.WriteLine($"Archivo generado: {ruta}");
        Console.WriteLine($"¿El archivo contiene algún DNI completo? {(contieneDni ? "Sí" : "No")}");
        Console.WriteLine($"Expedientes recuperados del archivo: {RepositorioJson.Cargar(ruta).Count}");
        File.Delete(ruta);

        Console.WriteLine("\n[Prototipo ejecutado con éxito para el informe 1.4]");
    }
}