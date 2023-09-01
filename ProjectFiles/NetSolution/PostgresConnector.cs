#region Using directives
using System;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.HMIProject;
using FTOptix.UI;
using FTOptix.Retentivity;
using FTOptix.NativeUI;
using FTOptix.Core;
using FTOptix.CoreBase;
using FTOptix.NetLogic;
using FTOptix.Store;
using FTOptix.ODBCStore;
using FTOptix.DataLogger;
using FTOptix.SQLiteStore;
using Npgsql;
using System.Data;
using FTOptix.Modbus;
using FTOptix.CommunicationDriver;
using System.Reflection;
using Renci.SshNet;
using System.Collections.Generic;
using FTOptix.OPCUAServer;
using System.Linq;
using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
#endregion

public class PostgresConnector : BaseNetLogic
{

    public override void Start()
    {
    }

    public override void Stop()
    {
    }

    [ExportMethod]
    public void Query(string query)
    {
        try
        {
            using var cmd = new NpgsqlCommand(query, postgresConn);
            var rowAffected = cmd.ExecuteNonQuery();
            DB_QUERIES_FEEDBACK.Value = "done. Row affected: " + rowAffected;
            Log.Info("Inset query affected " + rowAffected);
        }
        catch (System.Exception ex)
        {
            Log.Error(MethodBase.GetCurrentMethod().Name, ex.Message);
            DB_QUERIES_FEEDBACK.Value = ex.Message;
        }
    }

    [ExportMethod]
    public void SelectQuery(string query, NodeId containerId)
    {
        try
        {
            using var cmd = new NpgsqlCommand(query, postgresConn);

            var adapter = new NpgsqlDataAdapter(cmd);
            var dataTable = new DataTable();
            adapter.Fill(dataTable);

            CreateTableOnEmbeddedDatabase(dataTable);
            CreateDataGridFromDBTable(containerId);
            CreateHtmlGridFromDBTable(dataTable);

        }
        catch (System.Exception ex)
        {
            Log.Error(MethodBase.GetCurrentMethod().Name, ex.Message);
            DB_QUERIES_FEEDBACK.Value = ex.Message;
        }
    }

    [ExportMethod]
    public void InsertRandomData(int rows)
    {
        try
        {
            var query = "INSERT INTO MyTable (Variable1, Variable2, DateTimeUTC, DateTimeNow) VALUES (@var1, @var2, @dtutc, @dtnow);";
            using var cmd = new NpgsqlCommand(query, postgresConn);

            var random = new Random();
            for (int i = 0; i < rows; i++)
            {
                cmd.Parameters.AddWithValue("@var1", random.Next());
                cmd.Parameters.AddWithValue("@var2", Guid.NewGuid().ToString());
                cmd.Parameters.AddWithValue("@dtutc", DateTime.UtcNow);
                cmd.Parameters.AddWithValue("@dtnow", DateTime.Now);
                cmd.ExecuteNonQuery();
                cmd.Parameters.Clear();
            }
            DB_QUERIES_FEEDBACK.Value = "done.";
        }
        catch (System.Exception ex)
        {
            Log.Error(MethodBase.GetCurrentMethod().Name, ex.Message);
            DB_QUERIES_FEEDBACK.Value = ex.Message;
        }
    }

    [ExportMethod]
    public void ConnectToPostgresDB()
    {
        try
        {
            ParametersSetup();
            if (SSH.Value)
            {
                sshClient = new SshClient(SSH_HOSTNAME_IP, (int)SSH_PORT, SSH_USERNAME, SSH_PASSWORD);
                sshClient.Connect();
                SSH_CONNECTION_STATUS.Value = sshClient.IsConnected;

                if (sshClient.IsConnected)
                {
                    port = new ForwardedPortLocal("localhost", DB_SERVER_IP, DB_SERVER_PORT);
                    sshClient.AddForwardedPort(port);
                    port.Start();
                    if (port.IsStarted)
                    {
                        // if using ssh docker container
                        // /etc/ssh/
                        // nano sshd_config
                        // set Port es.: 2222
                        // set AllowTcpForwarding to 'yes'
                        // restart container
                        ConnectToPostgresServer(port.BoundHost, (int)port.BoundPort);
                    }
                }
            }
            else
            {
                ConnectToPostgresServer(DB_SERVER_IP, (int)DB_SERVER_PORT);
            }
        }
        catch (System.Exception ex)
        {
            Log.Error(MethodBase.GetCurrentMethod().Name, ex.Message);
            DisconnectFromPostgresDB();
        }
    }

    [ExportMethod]
    public void DisconnectFromPostgresDB()
    {
        port?.Stop();
        postgresConn?.Close();
        postgresConn.StateChange -= UpdateConnectionStatus;
        postgresConn?.Dispose();
        sshClient?.Disconnect();
        SSH_CONNECTION_STATUS.Value = sshClient.IsConnected;
    }

    #region private methods
    private void CreateDataGridFromDBTable(NodeId containerId)
    {
        try
        {
            var dataGridContainer = InformationModel.Get(containerId);
            dataGridContainer.Children.Clear();
            var dataGrid = InformationModel.Make<CustomDataGrid>("myDataGrid");
            dataGrid.Query = "SELECT * FROM " + TABLE_NAME + " ORDER BY id DESC";

            // var columnId = dataGrid.Columns.FirstOrDefault(c => c.BrowseName == "ColumnId");

            // var column1 = InformationModel.Make<DataGridColumn>("column1");

            // column1.DataItemTemplate = columnId.DataItemTemplate;

            // column1.Title = "Saluti";

            // dataGrid.Columns.Add(column1);

            dataGridContainer.Add(dataGrid);
        }
        catch (System.Exception ex)
        {
            Log.Error(MethodBase.GetCurrentMethod().Name, ex.Message);
        }
    }

    private void CreateHtmlGridFromDBTable(DataTable dataTable)
    {
        try
        {
            string newJsonData = JsonConvert.SerializeObject(dataTable, Formatting.Indented);

            // Get template path
            var templatePath = ResourceUri.FromProjectRelativePath("bootstrap_tables/Template-simple_table.html");
            // Read template page content
            string text = File.ReadAllText(templatePath.Uri);
            // Replace placeholders with data, $0i -> Value
            text = text.Replace("$jsonPlaceholder", newJsonData);

            templatePath = ResourceUri.FromProjectRelativePath(templatePath.Uri.Replace("Template-", ""));
            // Write to file
            File.WriteAllText(templatePath.Uri, text);
        }
        catch (System.Exception ex)
        {
            Log.Error(MethodBase.GetCurrentMethod().Name, ex.Message);
        }
    }
    private SQLiteStore GetSupportEmbeddedDB() => Project.Current.Get<SQLiteStore>("DataStores/SupportEmbeddedDB");
    private void CreateTableOnEmbeddedDatabase(DataTable dataTable)
    {
        try
        {
            var supportEmbeddedDb = GetSupportEmbeddedDB();

            if (supportEmbeddedDb.Tables.FirstOrDefault(t => t.BrowseName == TABLE_NAME) != null)
            {
                supportEmbeddedDb.RemoveTable(TABLE_NAME);
            }
            supportEmbeddedDb.AddTable(TABLE_NAME);

            var columnsNamesAndTypes = GetColumnsNamesAndTypes();

            foreach (var kv in columnsNamesAndTypes)
            {
                supportEmbeddedDb.AddColumn(TABLE_NAME, kv.Key, FromDbColumnTypeToOpcUaType(kv.Value));
            }

            int numRows = dataTable.Rows.Count;
            int numCols = dataTable.Columns.Count;
            object[,] dataArray = new object[numRows, numCols];

            for (int i = 0; i < numRows; i++)
            {
                DataRow row = dataTable.Rows[i];
                for (int j = 0; j < numCols; j++)
                {
                    dataArray[i, j] = row[j];
                }
            }

            supportEmbeddedDb.Insert(TABLE_NAME, dataTable.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToArray() , dataArray);
        }
        catch (System.Exception ex)
        {
            Log.Error(MethodBase.GetCurrentMethod().Name, ex.Message);
        }
    }

    private Dictionary<string, string> GetColumnsNamesAndTypes()
    {
        var columnsNamesAndTypes = new Dictionary<string, string>();

        using (var cmd = new NpgsqlCommand("SELECT column_name, data_type FROM information_schema.columns WHERE table_name = 'mytable'", postgresConn))
        {
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    string columnName = reader.GetString(0);
                    string dataType = reader.GetString(1);
                    columnsNamesAndTypes.Add(columnName, dataType);
                }
            }
        }
        return columnsNamesAndTypes;
    }

    private NodeId FromDbColumnTypeToOpcUaType(string dbType)
    {
        if (dbType.Contains("integer")) return OpcUa.DataTypes.Int32;
        if (dbType.Contains("timestamp")) return OpcUa.DataTypes.DateTime;
        if (dbType.Contains("character")) return OpcUa.DataTypes.String;
        return null;
    }

    private void ConnectToPostgresServer(string host, int port)
    {
        var connectionBuilder = new NpgsqlConnectionStringBuilder();
        connectionBuilder.Host = host;
        connectionBuilder.Port = port;
        connectionBuilder.Username = DB_USERNAME;
        connectionBuilder.Password = DB_PASSWORD;
        connectionBuilder.Database = DB_NAME;
        connectionBuilder.Timeout = 300;
        connectionBuilder.CommandTimeout = 300;
        postgresConn = new NpgsqlConnection(connectionBuilder.ConnectionString);

        postgresConn.StateChange += UpdateConnectionStatus;
        postgresConn.Open();
    }

    private void UpdateConnectionStatus(object sender, StateChangeEventArgs e)
    {
        Log.Info($"Postgress connection status changed from {e.OriginalState} to {e.CurrentState}");
        DB_CONNECTION_STATUS_VAR.Value = e.CurrentState == ConnectionState.Open;
    }

    private void ParametersSetup()
    {
        DB_CONNECTION_STATUS_VAR = LogicObject.GetVariable("Connection status");
        DB_SERVER_IP = LogicObject.GetVariable("Server IP").Value;
        DB_SERVER_PORT = uint.Parse(LogicObject.GetVariable("Server port").Value);
        DB_NAME = LogicObject.GetVariable("Database name").Value;
        DB_USERNAME = LogicObject.GetVariable("Username").Value;
        DB_PASSWORD = LogicObject.GetVariable("Password").Value;
        DB_QUERIES_FEEDBACK = LogicObject.GetVariable("Queries feedback");

        SSH = LogicObject.GetVariable("SSH enabled");
        SSH_ENABLED = LogicObject.GetVariable("SSH enabled").Value;
        SSH_HOSTNAME_IP = SSH.GetVariable("SSH hostname IP").Value;
        SSH_USERNAME = SSH.GetVariable("SSH username").Value;
        SSH_PASSWORD = SSH.GetVariable("SSH password").Value;
        SSH_PORT = SSH.GetVariable("SSH port").Value;
        SSH_CONNECTION_STATUS = SSH.GetVariable("SSH connection status");
    }

    private const string TABLE_NAME = "MyTable";

    private NpgsqlConnection postgresConn;
    private IUAVariable DB_CONNECTION_STATUS_VAR;
    private IUAVariable SSH_CONNECTION_STATUS;
    private IUAVariable DB_QUERIES_FEEDBACK;
    private string DB_SERVER_IP;
    private uint DB_SERVER_PORT;
    private string DB_NAME;
    private string DB_USERNAME;
    private string DB_PASSWORD;
    public string SSH_HOSTNAME_IP;
    public IUAVariable SSH;
    public bool SSH_ENABLED;
    public string SSH_USERNAME;
    private string SSH_PASSWORD;
    private uint SSH_PORT;
    private SshClient sshClient;
    private ForwardedPortLocal port;

    #endregion
}
