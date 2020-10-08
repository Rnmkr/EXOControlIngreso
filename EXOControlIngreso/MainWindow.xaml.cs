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
            LiveTimeTextBlock.Text = DateTime.Now.ToString("HH:mm");
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

                MessageLabel.Content = "ERROR INICIANDO";
            }

            SpeechSynth.Volume = 100;
            SpeechSynth.SelectVoiceByHints(VoiceGender.Female, VoiceAge.Senior);
            SpeechSynth.Rate = 2;
        }

        #endregion

        #region Initializers

        private void InitializeRemoteSqlConnection()
        {
            var RemoteConnectionString = @"Data Source=BUBBA; Initial Catalog=Produccion; Persist Security Info=True; User Id=BUBBASQL; Password=12345678; MultipleActiveResultSets=True";
            RemoteSqlConnection = new SqlConnection(RemoteConnectionString);
            RemoteSqlConnection.Open();
        }

        private void InitializeLocalDatabases()
        {
            UserList = GetRemoteUserList();
            LocalDatabaseSQLiteConnection = new SQLiteConnection(DateTime.Now.Year.ToString() + "attendance.db");
            LocalSyncDatabaseSQLiteConnection = new SQLiteConnection(DateTime.Now.Year.ToString() + "attendancesync.db");
            LocalDatabaseSQLiteConnection.CreateTable<Asistencia>();
            LocalSyncDatabaseSQLiteConnection.CreateTable<AsistenciaSync>();
        }

        private void InitializeTimers()
        {
            LiveDateTimeTimer.Interval = TimeSpan.FromSeconds(60);
            LiveDateTimeTimer.Tick += LiveDateTimeTimer_Tick;
            LiveDateTimeTimer.Start();

            ReadNewKeyInputTimer.Interval = TimeSpan.FromMilliseconds(700);
            ReadNewKeyInputTimer.Tick += ReadNewKeyInputTimer_Tick;

            RemoteDatabaseSyncTimer.Interval = TimeSpan.FromSeconds(60);
            RemoteDatabaseSyncTimer.Tick += RemoteDatabaseSyncTimer_Tick;
        }

        #endregion

        #region tickers

        private void LiveDateTimeTimer_Tick(object sender, EventArgs e)
        {
            LiveTimeTextBlock.Text = DateTime.Now.ToString("HH:mm");
            LiveDateTextBlock.Text = DateTime.Now.ToLongDateString();
        }

        private void ReadNewKeyInputTimer_Tick(object sender, EventArgs e)
        {
            ReadNewKeyInputTimer.Stop();
            KeyInput = string.Empty;
            MessageLabel.Content = "Escanée su código para identificarse";
        }

        private async void RemoteDatabaseSyncTimer_Tick(object sender, EventArgs e)
        {
            await Task.Run(() =>
            {
                SyncRemoteDatabase();
            });
        }

        #endregion

        #region events

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

                MessageLabel.Content = "Error obteniendo usuarios!";
            }
            finally
            {
                RemoteSqlConnection.Close();
            }
            return ul;
        }

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

        private void InsertNewAttendance(Usuario user)
        {
            try
            {
                Asistencia newAsistencia = new Asistencia { FK_IDPersonal = user.UserID, Fecha = DateTime.Now.ToShortDateString(), Hora = DateTime.Now.ToShortTimeString() };
                LocalDatabaseSQLiteConnection.Insert(newAsistencia);
                var lastInsertID = Convert.ToInt32(LocalDatabaseSQLiteConnection.ExecuteScalar<long>("SELECT last_insert_rowid() FROM Asistencia"));

                AsistenciaSync newAsistenciaSync = new AsistenciaSync { IDAsistencia = lastInsertID, FK_IDPersonal = user.UserID, Fecha = DateTime.Now.ToShortDateString(), Hora = DateTime.Now.ToShortTimeString() };
                LocalSyncDatabaseSQLiteConnection.Insert(newAsistenciaSync);

                MessageLabel.Content = "Correcto, " + user.UserSurname + " " + user.UserName;
                SpeakAsync("OK");
                RemoteDatabaseSyncTimer.Stop();
                RemoteDatabaseSyncTimer.Start();
            }
            catch (Exception e)
            {
                using (StreamWriter w = File.AppendText("ControlIngreso.log"))
                {
                    Log("ERROR SETNEWATTENDANCE" + Environment.NewLine + e.ToString() + Environment.NewLine, w);
                }

                MessageLabel.Content = "ERROR";
                SpeakAsync("ERROR");
            }
        }

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
            var lastRecords = LocalSyncDatabaseSQLiteConnection.Query<AsistenciaSync>("SELECT * FROM AsistenciaSync").ToList();
            if (lastRecords.Count == 0) return;

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
                    RemoteSyncSqlCommand.Parameters.Clear();
                }

                if (sqloutput)
                {
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
