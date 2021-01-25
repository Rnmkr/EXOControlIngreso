using SQLite;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Speech.Synthesis;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace EXOControlIngreso
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        #region Fields

        private string KeyInput;
        private SqlConnection RemoteSqlConnection;
        private SQLiteConnection LocalDatabaseSQLiteConnection;
        private SQLiteConnection LocalSyncDatabaseSQLiteConnection;
        private List<Usuario> UserList = new List<Usuario>();
        private DispatcherTimer LiveDateTimeTimer = new DispatcherTimer();
        private DispatcherTimer ReadNewKeyInputTimer = new DispatcherTimer();
        private DispatcherTimer RemoteDatabaseSyncTimer = new DispatcherTimer();
        private SpeechSynthesizer SpeechSynth = new SpeechSynthesizer();
        private Regex OnlyDigitsRegex = new Regex(@"^[D]\d$");

        #endregion

        #region ctor
        public MainWindow()
        {
            InitializeComponent();
            //CheckAuth();

            VersionTextBlock.Text = Assembly.GetExecutingAssembly().GetName().Version.ToString();
            LiveHourTextBlock.Text = DateTime.Now.ToString("HH");
            LiveMinutesTextBlock.Text = DateTime.Now.ToString("mm");

            LiveDateTextBlock.Text = DateTime.Now.ToLongDateString();

            try
            {
                InitializeRemoteSqlConnection();
                InitializeLocalDatabases();
                InitializeTimers();
            }
            catch (Exception e)
            {
                using (StreamWriter w = File.AppendText("ControlIngreso.log"))
                {
                    Log("ERROR CTOR" + Environment.NewLine + e.ToString() + Environment.NewLine, w);
                }

                MessageLabel.Content = "ERROR INICIANDO. REINICIE EL EQUIPO.";
            }

            SpeechSynth.Volume = 100;
            SpeechSynth.SelectVoiceByHints(VoiceGender.Female, VoiceAge.Senior);
            SpeechSynth.Rate = 2;
        }

        #endregion

        #region Initializers

        //Abre la conexion SQL para reducir el tiempo del primer fichado
        private void InitializeRemoteSqlConnection()
        {
            var RemoteConnectionString = @"Data Source=BUBBA; Initial Catalog=Produccion; Persist Security Info=True; User Id=BUBBASQL; Password=12345678; MultipleActiveResultSets=True";
            RemoteSqlConnection = new SqlConnection(RemoteConnectionString);
            RemoteSqlConnection.Open();
        }

        //Si no existen las bases de datos las crea
        private void InitializeLocalDatabases()
        {
            UserList = GetRemoteUserList();
            LocalDatabaseSQLiteConnection = new SQLiteConnection("attendance.db");
            LocalSyncDatabaseSQLiteConnection = new SQLiteConnection("attendancesync.db");
            LocalDatabaseSQLiteConnection.CreateTable<Asistencia>();
            LocalSyncDatabaseSQLiteConnection.CreateTable<AsistenciaSync>();
        }


        private void InitializeTimers()
        {
            //Actualiza el reloj
            LiveDateTimeTimer.Interval = TimeSpan.FromSeconds(1);
            LiveDateTimeTimer.Tick += LiveDateTimeTimer_Tick;
            LiveDateTimeTimer.Start();

            //Reinicia la espera de un nuevo fichado desde el ultimo intento
            ReadNewKeyInputTimer.Interval = TimeSpan.FromMilliseconds(700);
            ReadNewKeyInputTimer.Tick += ReadNewKeyInputTimer_Tick;

            //Inicia la sincronizacion con la BD a partir del ultimo fichado correcto
            RemoteDatabaseSyncTimer.Interval = TimeSpan.FromSeconds(60);
            RemoteDatabaseSyncTimer.Tick += RemoteDatabaseSyncTimer_Tick;
        }

        #endregion

        #region tickers

        //Actualiza el reloj en pantalla
        private void LiveDateTimeTimer_Tick(object sender, EventArgs e)
        {
            if (LiveDotsTextBlock.Visibility == Visibility.Visible)
            {
                LiveDotsTextBlock.Visibility = Visibility.Hidden;
            }
            else
            {
                LiveDotsTextBlock.Visibility = Visibility.Visible;
            }

            LiveHourTextBlock.Text = DateTime.Now.ToString("HH");
            LiveMinutesTextBlock.Text = DateTime.Now.ToString("mm");

            LiveDateTextBlock.Text = DateTime.Now.ToLongDateString();
        }

        //Resetea la espera de un nuevo fichado
        private void ReadNewKeyInputTimer_Tick(object sender, EventArgs e)
        {
            ReadNewKeyInputTimer.Stop();
            KeyInput = string.Empty;
            MessageLabel.Content = "Utilice su código para identificarse";
        }

        //Inicia la sincronizacion con el servidor
        private async void RemoteDatabaseSyncTimer_Tick(object sender, EventArgs e)
        {
            await Task.Run(() =>
            {
                SyncRemoteDatabase();
            });
        }

        #endregion

        #region events

        //Procesa la lectura del codigo de barras
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var key = e.Key.ToString();

            if (!OnlyDigitsRegex.IsMatch(key))
            {
                return;
            }

            if (string.IsNullOrEmpty(KeyInput))
            {
                ReadNewKeyInputTimer.Start();
            }

            KeyInput += key.Substring(1, 1);

            if (KeyInput.Length == 5)
            {
                SetAttendance();
            }
        }

        #endregion

        #region methods

        private void CheckAuth()
        {
            try
            {
                Microsoft.Win32.RegistryKey RegKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey("SOFTWARE", true);
                string KeyPath = "E86C-5ECE-7E3B-045D-2194-58B5-B6B2-BC04";
                RegKey = RegKey.OpenSubKey(KeyPath, true);
                var regMUID = RegKey.GetValue("MUID").ToString();
                var newMUID = "E86C-5ECE-7E3B-045D-2194-58B5-B6B2-BC04";
                if (newMUID == regMUID)
                {
                    return;
                }
                else
                {
                    MessageLabel.Content = "ERROR INICIANDO";
                }
            }
            catch (Exception e)
            {
                using (StreamWriter w = File.AppendText("ControlIngreso.log"))
                {
                    Log("ERROR CHECKAUTH" + Environment.NewLine + e.ToString() + Environment.NewLine, w);
                }

                MessageLabel.Content = "ERROR INICIANDO";
                return;
            }
        }


        //Obtiene la lista de usuarios desde el server y la tiene en memoria para un acceso mas rapido
        private List<Usuario> GetRemoteUserList()
        {
            var ul = new List<Usuario>();

            try
            {
                var GetUsersFromRemoteServer = new SqlCommand("GetAllUsers", RemoteSqlConnection);
                GetUsersFromRemoteServer.CommandType = CommandType.StoredProcedure;
                SqlDataReader reader = GetUsersFromRemoteServer.ExecuteReader();

                while (reader.Read())
                {
                    Usuario user = new Usuario();
                    user.UserID = Convert.ToInt32(reader["ID"]);
                    user.UserAccessCode = reader["NumeroAcceso"].ToString().TrimEnd();
                    user.UserSurname = reader["Apellido"].ToString().TrimEnd();
                    user.UserName = reader["Nombre"].ToString().TrimEnd();
                    ul.Add(user);
                }
            }
            catch (Exception e)
            {
                using (StreamWriter w = File.AppendText("ControlIngreso.log"))
                {
                    Log("ERROR GETRREMOTEUSERLIST" + Environment.NewLine + e.ToString() + Environment.NewLine, w);
                }

                MessageLabel.Content = "Error obteniendo usuarios! Reinicie el equipo.";
            }
            finally
            {
                RemoteSqlConnection.Close();
            }
            return ul;
        }


        //Busca el codigo de fichado en la lista de usuarios y llama al metodo que guarda el fichado (InsertNewAttendance)
        private void SetAttendance()
        {
            ReadNewKeyInputTimer.Stop();

            MessageLabel.Content = "Identificando...";

            if (UserList.Any(a => a.UserAccessCode == KeyInput))
            {
                Usuario user = UserList.Where(w => w.UserAccessCode == KeyInput).Select(s => s).FirstOrDefault();
                InsertNewAttendance(user);
            }
            else
            {
                MessageLabel.Content = "No se encontró el usuario!";
                SpeakAsync("ERROR");
            }

            ReadNewKeyInputTimer.Start();
        }

        //Guarda el fichado
        private void InsertNewAttendance(Usuario user)
        {
            try
            {
                //Crea una nueva asistencia para la base de datos local (mirror)
                Asistencia newAsistencia = new Asistencia { FK_IDPersonal = user.UserID, Fecha = DateTime.Now.ToShortDateString(), Hora = DateTime.Now.ToShortTimeString() };
                LocalDatabaseSQLiteConnection.Insert(newAsistencia);

                //Crea una nueva asistencia para la base de datos de sincronizacion con el ultimo ID obtenido de la DB mirror
                var lastInsertID = Convert.ToInt32(LocalDatabaseSQLiteConnection.ExecuteScalar<long>("SELECT last_insert_rowid() FROM Asistencia"));
                AsistenciaSync newAsistenciaSync = new AsistenciaSync { IDAsistencia = lastInsertID, FK_IDPersonal = newAsistencia.FK_IDPersonal, Fecha = newAsistencia.Fecha, Hora = newAsistencia.Hora };
                LocalSyncDatabaseSQLiteConnection.Insert(newAsistenciaSync);

                MessageLabel.Content = "Correcto, " + user.UserSurname + " " + user.UserName;
                SpeakAsync("OK");

                //Inicia el conteo desde el ultimo fichado correcto para sincronizar la BD de sincronizacion con el server
                RemoteDatabaseSyncTimer.Stop(); 
                RemoteDatabaseSyncTimer.Start();
            }
            catch (Exception e)
            {
                using (StreamWriter w = File.AppendText("ControlIngreso.log"))
                {
                    Log("ERROR INSERTNEWATTENDANCE" + Environment.NewLine + e.ToString() + Environment.NewLine, w);
                }

                MessageLabel.Content = "ERROR";
                SpeakAsync("ERROR");
            }
        }

        //Sincroniza la DB con el server
        private void SyncRemoteDatabase()
        {
            try
            {
                InitializeRemoteSqlConnection();
                ProcessSyncData();
            }
            catch (Exception ex)
            {
                using (StreamWriter w = File.AppendText("ControlIngreso.log"))
                {
                    Log("ERROR SYNCREMOTEDATABASE" + Environment.NewLine + ex.ToString() + Environment.NewLine, w);
                }
                Console.WriteLine(ex.ToString());
            }
            finally
            {
                RemoteSqlConnection.Close();
                RemoteDatabaseSyncTimer.Stop();
            }
        }

        private void ProcessSyncData()
        {
            //Obtengo la lista de ultimos fichados en la DB de sincronizacion
            var lastRecords = LocalSyncDatabaseSQLiteConnection.Query<AsistenciaSync>("SELECT * FROM AsistenciaSync").ToList();
            if (lastRecords.Count == 0) return;

            //Exporto al sercer
            var RemoteSyncSqlCommand = new SqlCommand("SyncAttendance", RemoteSqlConnection);
            RemoteSyncSqlCommand.CommandType = CommandType.StoredProcedure;


            foreach (var record in lastRecords)
            {
                RemoteSyncSqlCommand.Parameters.AddWithValue("@IDAsistencia", record.IDAsistencia);
                RemoteSyncSqlCommand.Parameters.AddWithValue("@FK_IDPersonal", record.FK_IDPersonal);
                RemoteSyncSqlCommand.Parameters.AddWithValue("@Fecha", Convert.ToDateTime(record.Fecha).ToString("M/d/yyyy"));
                RemoteSyncSqlCommand.Parameters.AddWithValue("@Hora", Convert.ToDateTime(record.Hora).ToString("HH:mm"));
                RemoteSyncSqlCommand.Parameters.Add("@Output", SqlDbType.Bit);
                RemoteSyncSqlCommand.Parameters["@Output"].Direction = ParameterDirection.Output;
                bool sqloutput = false;
                try
                {
                    RemoteSyncSqlCommand.ExecuteNonQuery();
                    sqloutput = (bool)RemoteSyncSqlCommand.Parameters["@Output"].Value;
                }
                catch (SqlException e)
                {
                    //Si el registro ya existe en el server, lo borro de la DB de sincronizacion
                    if (e.Message.Contains("Violation of PRIMARY KEY constraint"))
                    {
                        sqloutput = true;
                    }
                    else
                    {
                        using (StreamWriter w = File.AppendText("ControlIngreso.log"))
                        {
                            Log("ERROR SYNCREMOTEDATABASE:INSERT" + Environment.NewLine + e.ToString() + Environment.NewLine, w);
                        }
                    }

                }
                finally
                {
                    //Limpio los parametros del query
                    RemoteSyncSqlCommand.Parameters.Clear();
                }

                if (sqloutput)
                {
                    //Borro el registro de la base de datos de sincronizacion si se exporto al server correctamente
                    LocalSyncDatabaseSQLiteConnection.Delete<AsistenciaSync>(record.IDAsistencia);
                }
            }
        }

        private async void SpeakAsync(string words)
        {
            await Task.Run(() =>
            {
                SpeechSynth.Speak(words);
            });
        }


        //https://docs.microsoft.com/en-us/dotnet/standard/io/how-to-open-and-append-to-a-log-file
        public static void Log(string logMessage, TextWriter tw)
        {
            //w.Write("\r\nLog Entry : ");
            tw.WriteLine($"{DateTime.Now.ToLongTimeString()} {DateTime.Now.ToLongDateString()}");
            tw.WriteLine($"  :{logMessage}");
            tw.WriteLine("-------------------------------");
        }

        #endregion

        #region classes

        private class Usuario
        {
            public int UserID { get; set; }
            public string UserAccessCode { get; set; }
            public string UserName { get; set; }
            public string UserSurname { get; set; }
        }
        private class Asistencia
        {
            //reemplazados por rowid de sqlite, solo para la db local
            //[PrimaryKey, AutoIncrement]
            //public int IDAsistencia { get; set; }
            public int FK_IDPersonal { get; set; }
            public string Fecha { get; set; }
            public string Hora { get; set; }
        }
        private class AsistenciaSync
        {
            [PrimaryKey]
            public int IDAsistencia { get; set; }
            public int FK_IDPersonal { get; set; }
            public string Fecha { get; set; }
            public string Hora { get; set; }
        }

        #endregion
    }
}
