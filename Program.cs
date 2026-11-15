using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MesaPartesEsSalud
{
    // --- 1. ENUMERACIONES ---
    public enum Estado { Recibido, Derivado, Atendido, Archivado }
    public enum TipoTramite { Subsidio, Reclamo, HistoriaClinica }

    // --- 2. CLASE ASEGURADO (Ley N.º 29733 - Protección de Datos) ---
    public class Asegurado
    {
        private string nombreCompleto;
        private string dniHash;
        private string dniEnmascarado;

        public Asegurado(string dniPlano, string nombre)
        {
            if (!ValidarDni(dniPlano))
                throw new ArgumentException("El DNI debe tener exactamente 8 dígitos numéricos.");

            nombreCompleto = nombre;
            dniEnmascarado = "****" + dniPlano.Substring(4, 4); // Enmascaramiento para proteger datos
            dniHash = GenerarHashSha256(dniPlano);
        }

        public static bool ValidarDni(string dni)
        {
            return !string.IsNullOrEmpty(dni) && dni.Length == 8 && dni.All(char.IsDigit);
        }

        private string GenerarHashSha256(string texto)
        {
            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(texto));
                return Convert.ToBase64String(bytes);
            }
        }

        public string NombreCompleto => nombreCompleto;
        public string DniEnmascarado => dniEnmascarado;
    }

    // --- 3. CLASE MOVIMIENTO HISTORIAL ---
    public class MovimientoHistorial
    {
        public Estado Estado { get; }
        public DateTime Fecha { get; }
        public string Responsable { get; }
        public string Observacion { get; }

        public MovimientoHistorial(Estado estado, string responsable, string observacion)
        {
            Estado = estado;
            Fecha = DateTime.Now;
            Responsable = responsable;
            Observacion = observacion;
        }
    }

    // --- 4. CLASE ABSTRACTA EXPEDIENTE ---
    public abstract class Expediente
    {
        public string Codigo { get; protected set; }
        public DateTime FechaRegistro { get; protected set; }
        public Estado EstadoActual { get; protected set; }
        public Asegurado Asegurado { get; protected set; }
        public List<MovimientoHistorial> Historial { get; protected set; }

        public abstract int PlazoDias { get; }

        protected Expediente(string codigo, Asegurado asegurado)
        {
            Codigo = codigo;
            FechaRegistro = DateTime.Now;
            EstadoActual = Estado.Recibido;
            Asegurado = asegurado;
            Historial = new List<MovimientoHistorial>
            {
                new MovimientoHistorial(Estado.Recibido, "Sistema Ventanilla", "Expediente registrado en mesa de partes.")
            };
        }

        public DateTime FechaLimite() => FechaRegistro.AddDays(PlazoDias);

        public void CambiarEstado(Estado nuevoEstado, string responsable, string observacion)
        {
            // Validar secuencia lógica de estados (Control de negocio)
            if (EstadoActual == Estado.Archivado)
                throw new InvalidOperationException("No se puede modificar un expediente ya archivado.");

            EstadoActual = nuevoEstado;
            Historial.Add(new MovimientoHistorial(nuevoEstado, responsable, observacion));
        }

        public bool EstaVencido(DateTime hoy)
        {
            return EstadoActual != Estado.Atendido && EstadoActual != Estado.Archivado && hoy > FechaLimite();
        }

        public int? DiasDeAtencion()
        {
            var atendido = Historial.FirstOrDefault(h => h.Estado == Estado.Atendido);
            if (atendido != null)
                return (int)(atendido.Fecha - FechaRegistro).TotalDays;
            return null;
        }
    }

    // --- 5. SUBCLASES DE EXPEDIENTE ---
    public class ExpedienteSubsidio : Expediente
    {
        public int DiasIncapacidad { get; }
        public override int PlazoDias => 15; // Plazo según reglamento interno

        public ExpedienteSubsidio(string codigo, Asegurado asegurado, int diasIncapacidad) : base(codigo, asegurado)
        {
            DiasIncapacidad = diasIncapacidad;
        }
    }

    public class ExpedienteReclamo : Expediente
    {
        public string MotivoReclamo { get; }
        public override int PlazoDias => 10;

        public ExpedienteReclamo(string codigo, Asegurado asegurado, string motivo) : base(codigo, asegurado)
        {
            MotivoReclamo = motivo;
        }
    }

    public class ExpedienteHistoriaClinica : Expediente
    {
        public string NroHistoria { get; }
        public override int PlazoDias => 5;

        public ExpedienteHistoriaClinica(string codigo, Asegurado asegurado, string nroHistoria) : base(codigo, asegurado)
        {
            NroHistoria = nroHistoria;
        }
    }

    // --- 6. PATRÓN FACTORY (Fábrica Simple) ---
    public static class ExpedienteFactory
    {
        public static Expediente Crear(TipoTramite tipo, string codigo, Asegurado asegurado, string detalle)
        {
            return tipo switch
            {
                TipoTramite.Subsidio => new ExpedienteSubsidio(codigo, asegurado, 15),
                TipoTramite.Reclamo => new ExpedienteReclamo(codigo, asegurado, detalle),
                TipoTramite.HistoriaClinica => new ExpedienteHistoriaClinica(codigo, asegurado, detalle),
                _ => throw new ArgumentException("Tipo de trámite no válido.")
            };
        }
    }

    // --- 7. PATRÓN OBSERVER (Argumentos de Evento) ---
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

    // --- 8. PATRÓN SINGLETON (Gestor de Mesa de Partes) ---
    public sealed class GestorMesaPartes
    {
        private static readonly Lazy<GestorMesaPartes> instancia = new(() => new GestorMesaPartes());
        public static GestorMesaPartes Instancia => instancia.Value;

        private List<Expediente> expedientes;
        private int correlativoActual;

        public event EventHandler<PlazoVencidoEventArgs> AlertaPlazoVencido;

        private GestorMesaPartes()
        {
            expedientes = new List<Expediente>();
            correlativoActual = 1;
        }

        public Expediente RegistrarExpediente(TipoTramite tipo, string dni, string nombre, string detalle)
        {
            try
            {
                string codigo = $"ESS-{correlativoActual:D3}";
                Asegurado asegurado = new Asegurado(dni, nombre);
                Expediente nuevo = ExpedienteFactory.Crear(tipo, codigo, asegurado, detalle);
                
                expedientes.Add(nuevo);
                correlativoActual++;
                return nuevo;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error de Registro Controlado]: {ex.Message}");
                return null;
            }
        }

        public Expediente BuscarPorCodigo(string codigo)
        {
            return expedientes.FirstOrDefault(e => e.Codigo.Equals(codigo, StringComparison.OrdinalIgnoreCase));
        }

        public void VerificarVencimientos(DateTime hoy)
        {
            foreach (var exp in expedientes)
            {
                if (exp.EstaVencido(hoy))
                {
                    int retraso = (int)(hoy - exp.FechaLimite()).TotalDays;
                    AlertaPlazoVencido?.Invoke(this, new PlazoVencidoEventArgs(exp, retraso));
                }
            }
        }

        public List<Expediente> ObtenerTodos() => expedientes;
    }

    // --- 9. SERVICIO ESTÁTICO DE REPORTES (Uso de LINQ / Funciones de Orden Superior) ---
    public static class ReporteExpedientes
    {
        public static List<Expediente> Pendientes(List<Expediente> lista)
        {
            // Función de orden superior (Where de LINQ)
            return lista.Where(e => e.EstadoActual != Estado.Atendido && e.EstadoActual != Estado.Archivado).ToList();
        }

        public static List<Expediente> Vencidos(List<Expediente> lista, DateTime hoy)
        {
            return lista.Where(e => e.EstaVencido(hoy)).ToList();
        }

        public static Dictionary<Estado, int> ConteoPorEstado(List<Expediente> lista)
        {
            // Función de orden superior (GroupBy y ToDictionary)
            return lista.GroupBy(e => e.EstadoActual)
                        .ToDictionary(g => g.Key, g => g.Count());
        }
    }

    // --- 10. EJECUCIÓN PRINCIPAL (Program) ---
    class Program
    {
        static void Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine("=== SISTEMA MESA DE PARTES DIGITAL - ESSALUD INDEPENDENCIA ===");

            var gestor = GestorMesaPartes.Instancia;

            // Suscripción al Patrón Observer (Evento de plazos vencidos)
            gestor.AlertaPlazoVencido += (sender, e) =>
            {
                Console.WriteLine($"[¡ALERTA PLAZO!] Expediente {e.Expediente.Codigo} vencido por {e.DiasDeRetraso} días.");
            };

            // Registro de expedientes de prueba
            var exp1 = gestor.RegistrarExpediente(TipoTramite.Subsidio, "45871234", "Maria Quispe", "Subsidio por incapacidad");
            var exp2 = gestor.RegistrarExpediente(TipoTramite.Reclamo, "71234567", "Juan Perez", "Demora en entrega de medicamentos");

            // Demostración de Tratamiento Seguro de Datos Personales (Ley 29733)
            if (exp1 != null)
            {
                Console.WriteLine($"\nRegistro exitoso: {exp1.Codigo} | Asegurado: {exp1.Asegurado.NombreCompleto} | DNI Seguro: {exp1.Asegurado.DniEnmascarado}");
            }

            // USO DE FUNCIONES DE ORDEN SUPERIOR (LINQ) para reportes
            var listaPendientes = ReporteExpedientes.Pendientes(gestor.ObtenerTodos());
            Console.WriteLine($"\n--- TOTAL DE EXPEDIENTES PENDIENTES: {listaPendientes.Count} ---");

            // Manejo de errores y excepciones controlado con try-catch
            try
            {
                Console.WriteLine("\nIntentando registrar un DNI inválido (debe fallar por validación):");
                gestor.RegistrarExpediente(TipoTramite.HistoriaClinica, "123", "Carlos Ruiz", "HC-992");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Excepción capturada correctamente: {ex.Message}");
            }

            Console.WriteLine("\n[Prototipo ejecutado con éxito para el informe 1.4]");
        }
    }
}