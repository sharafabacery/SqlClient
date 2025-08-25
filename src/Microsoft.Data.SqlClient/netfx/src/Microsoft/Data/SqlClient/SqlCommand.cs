// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.Data.Common;
using System.Data.SqlTypes;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.ConstrainedExecution;
using System.Security.Permissions;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Buffers;
using Microsoft.Data.Common;
using Microsoft.Data.Sql;
using Microsoft.Data.SqlClient.Server;
using System.Transactions;
using System.Collections.Concurrent;

// NOTE: The current Microsoft.VSDesigner editor attributes are implemented for System.Data.SqlClient, and are not publicly available.
// New attributes that are designed to work with Microsoft.Data.SqlClient and are publicly documented should be included in future.
namespace Microsoft.Data.SqlClient
{
    /// <include file='../../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlCommand.xml' path='docs/members[@name="SqlCommand"]/SqlCommand/*'/>
    [DefaultEvent("RecordsAffected")]
    [ToolboxItem(true)]
    [DesignerCategory("")]
    // TODO: Add designer attribute when Microsoft.VSDesigner.Data.VS.SqlCommandDesigner uses Microsoft.Data.SqlClient
    public sealed class SqlCommand : DbCommand, ICloneable
    {
        private static int _objectTypeCount; // EventSource Counter
        private const int MaxRPCNameLength = 1046;
        internal readonly int ObjectID = Interlocked.Increment(ref _objectTypeCount);

        internal sealed class ExecuteReaderAsyncCallContext : AAsyncCallContext<SqlCommand, SqlDataReader, CancellationTokenRegistration>
        {
            public Guid OperationID;
            public CommandBehavior CommandBehavior;

            public SqlCommand Command => _owner;
            public TaskCompletionSource<SqlDataReader> TaskCompletionSource => _source;

            public void Set(SqlCommand command, TaskCompletionSource<SqlDataReader> source, CancellationTokenRegistration disposable, CommandBehavior behavior, Guid operationID)
            {
                base.Set(command, source, disposable);
                CommandBehavior = behavior;
                OperationID = operationID;
            }

            protected override void Clear()
            {
                OperationID = default;
                CommandBehavior = default;
            }

            protected override void AfterCleared(SqlCommand owner)
            {
                owner?.SetCachedCommandExecuteReaderAsyncContext(this);
            }
        }

        internal sealed class ExecuteNonQueryAsyncCallContext : AAsyncCallContext<SqlCommand, int, CancellationTokenRegistration>
        {
            public Guid OperationID;

            public SqlCommand Command => _owner;

            public TaskCompletionSource<int> TaskCompletionSource => _source;

            public void Set(SqlCommand command, TaskCompletionSource<int> source, CancellationTokenRegistration disposable, Guid operationID)
            {
                base.Set(command, source, disposable);
                OperationID = operationID;
            }

            protected override void Clear()
            {
                OperationID = default;
            }

            protected override void AfterCleared(SqlCommand owner)
            {
                owner?.SetCachedCommandExecuteNonQueryAsyncContext(this);
            }
        }

        internal sealed class ExecuteXmlReaderAsyncCallContext : AAsyncCallContext<SqlCommand, XmlReader, CancellationTokenRegistration>
        {
            public Guid OperationID;

            public SqlCommand Command => _owner;
            public TaskCompletionSource<XmlReader> TaskCompletionSource => _source;

            public void Set(SqlCommand command, TaskCompletionSource<XmlReader> source, CancellationTokenRegistration disposable, Guid operationID)
            {
                base.Set(command, source, disposable);
                OperationID = operationID;
            }

            protected override void Clear()
            {
                OperationID = default;
            }

            protected override void AfterCleared(SqlCommand owner)
            {
                owner?.SetCachedCommandExecuteXmlReaderContext(this);
            }
        }

        private string _commandText;
        private CommandType _commandType;
        private int? _commandTimeout;
        private UpdateRowSource _updatedRowSource = UpdateRowSource.Both;
        private bool _designTimeInvisible;

        /// <summary>
        /// Indicates if the column encryption setting was set at-least once in the batch rpc mode, when using AddBatchCommand.
        /// </summary>
        private bool _wasBatchModeColumnEncryptionSettingSetOnce;

        /// <summary>
        /// Column Encryption Override. Defaults to SqlConnectionSetting, in which case
        /// it will be Enabled if SqlConnectionOptions.IsColumnEncryptionSettingEnabled = true, Disabled if false.
        /// This may also be used to set other behavior which overrides connection level setting.
        /// </summary>
        private SqlCommandColumnEncryptionSetting _columnEncryptionSetting = SqlCommandColumnEncryptionSetting.UseConnectionSetting;

        internal SqlDependency _sqlDep;

#if DEBUG
        /// <summary>
        /// Force the client to sleep during sp_describe_parameter_encryption in the function TryFetchInputParameterEncryptionInfo.
        /// </summary>
        private static bool _sleepDuringTryFetchInputParameterEncryptionInfo = false;

        /// <summary>
        /// Force the client to sleep during sp_describe_parameter_encryption in the function RunExecuteReaderTds.
        /// </summary>
        private static bool _sleepDuringRunExecuteReaderTdsForSpDescribeParameterEncryption = false;

        /// <summary>
        /// Force the client to sleep during sp_describe_parameter_encryption after ReadDescribeEncryptionParameterResults.
        /// </summary>
        private static bool _sleepAfterReadDescribeEncryptionParameterResults = false;

        /// <summary>
        /// Internal flag for testing purposes that forces all queries to internally end async calls.
        /// </summary>
        private static bool _forceInternalEndQuery = false;

        /// <summary>
        /// Internal flag for testing purposes that forces one RetryableEnclaveQueryExecutionException during GenerateEnclavePackage
        /// </summary>
        private static bool _forceRetryableEnclaveQueryExecutionExceptionDuringGenerateEnclavePackage = false;
#endif
        internal static readonly Action<object> s_cancelIgnoreFailure = CancelIgnoreFailureCallback;

        // Prepare
        // Against 7.0 Serve a prepare/unprepare requires an extra roundtrip to the server.
        //
        // From 8.0 and above, the preparation can be done as part of the command execution.

        private enum EXECTYPE
        {
            UNPREPARED,         // execute unprepared commands, all server versions (results in sp_execsql call)
            PREPAREPENDING,     // prepare and execute command, 8.0 and above only  (results in sp_prepexec call)
            PREPARED,           // execute prepared commands, all server versions   (results in sp_exec call)
        }

        // devnotes
        //
        // _hiddenPrepare
        // On 8.0 and above the Prepared state cannot be left. Once a command is prepared it will always be prepared.
        // A change in parameters, commandtext etc (IsDirty) automatically causes a hidden prepare
        //
        // _inPrepare will be set immediately before the actual prepare is done.
        // The OnReturnValue function will test this flag to determine whether the returned value is a _prepareHandle or something else.
        //
        // _prepareHandle - the handle of a prepared command. Apparently there can be multiple prepared commands at a time - a feature that we do not support yet.

        private static readonly object s_cachedInvalidPrepareHandle = (object)-1;
        private bool _inPrepare = false;
        private object _prepareHandle = s_cachedInvalidPrepareHandle; // this is an int which is used in the object typed SqlParameter.Value field, avoid repeated boxing by storing in a box
        private bool _hiddenPrepare = false;
        private int _preparedConnectionCloseCount = -1;
        private int _preparedConnectionReconnectCount = -1;

        private SqlParameterCollection _parameters;
        private SqlConnection _activeConnection;
        private bool _dirty = false;               // true if the user changes the commandtext or number of parameters after the command is already prepared
        private EXECTYPE _execType = EXECTYPE.UNPREPARED; // by default, assume the user is not sharing a connection so the command has not been prepared
        private _SqlRPC[] _rpcArrayOf1 = null;                // Used for RPC executes
        private _SqlRPC _rpcForEncryption = null;                // Used for sp_describe_parameter_encryption RPC executes

        // cut down on object creation and cache all these
        // cached metadata
        private _SqlMetaDataSet _cachedMetaData;

        // @TODO: Make properties
        internal ConcurrentDictionary<int, SqlTceCipherInfoEntry> keysToBeSentToEnclave;
        internal bool requiresEnclaveComputations = false;

        private bool ShouldCacheEncryptionMetadata
        {
            get
            {
                return !requiresEnclaveComputations || _activeConnection.Parser.AreEnclaveRetriesSupported;
            }
        }

        internal EnclavePackage enclavePackage = null;
        private SqlEnclaveAttestationParameters enclaveAttestationParameters = null;
        private byte[] customData = null;
        private int customDataLength = 0;

        // Last TaskCompletionSource for reconnect task - use for cancellation only
        private TaskCompletionSource<object> _reconnectionCompletionSource = null;

#if DEBUG
        internal static int DebugForceAsyncWriteDelay { get; set; }
#endif
        internal bool InPrepare
        {
            get
            {
                return _inPrepare;
            }
        }

        /// <summary>
        /// Return if column encryption setting is enabled.
        /// The order in the below if is important since _activeConnection.Parser can throw if the
        /// underlying tds connection is closed and we don't want to change the behavior for folks
        /// not trying to use transparent parameter encryption i.e. who don't use (SqlCommandColumnEncryptionSetting.Enabled or _activeConnection.IsColumnEncryptionSettingEnabled) here.
        /// </summary>
        internal bool IsColumnEncryptionEnabled
        {
            get
            {
                return (_columnEncryptionSetting == SqlCommandColumnEncryptionSetting.Enabled
                        || (_columnEncryptionSetting == SqlCommandColumnEncryptionSetting.UseConnectionSetting && _activeConnection.IsColumnEncryptionSettingEnabled))
                       && _activeConnection.Parser != null
                       && _activeConnection.Parser.IsColumnEncryptionSupported;
            }
        }

        internal bool ShouldUseEnclaveBasedWorkflow =>
            (!string.IsNullOrWhiteSpace(_activeConnection.EnclaveAttestationUrl) || Connection.AttestationProtocol == SqlConnectionAttestationProtocol.None) &&
                  IsColumnEncryptionEnabled;

        /// <summary>
        /// Per-command custom providers. It can be provided by the user and can be set more than once. 
        /// </summary> 
        private IReadOnlyDictionary<string, SqlColumnEncryptionKeyStoreProvider> _customColumnEncryptionKeyStoreProviders;

        internal bool HasColumnEncryptionKeyStoreProvidersRegistered =>
            _customColumnEncryptionKeyStoreProviders is not null && _customColumnEncryptionKeyStoreProviders.Count > 0;

        // Cached info for async executions
        private sealed class AsyncState
        {
            // @TODO: Autoproperties
            private int _cachedAsyncCloseCount = -1;    // value of the connection's CloseCount property when the asyncResult was set; tracks when connections are closed after an async operation
            private TaskCompletionSource<object> _cachedAsyncResult = null;
            private SqlConnection _cachedAsyncConnection = null;  // Used to validate that the connection hasn't changed when end the connection;
            private SqlDataReader _cachedAsyncReader = null;
            private RunBehavior _cachedRunBehavior = RunBehavior.ReturnImmediately;
            private string _cachedSetOptions = null;
            private string _cachedEndMethod = null;

            internal AsyncState()
            {
            }

            internal SqlDataReader CachedAsyncReader
            {
                get { return _cachedAsyncReader; }
            }
            internal RunBehavior CachedRunBehavior
            {
                get { return _cachedRunBehavior; }
            }
            internal string CachedSetOptions
            {
                get { return _cachedSetOptions; }
            }
            internal bool PendingAsyncOperation
            {
                get { return _cachedAsyncResult != null; }
            }
            internal string EndMethodName
            {
                get { return _cachedEndMethod; }
            }

            internal bool IsActiveConnectionValid(SqlConnection activeConnection)
            {
                return (_cachedAsyncConnection == activeConnection && _cachedAsyncCloseCount == activeConnection.CloseCount);
            }

            [ReliabilityContract(Consistency.WillNotCorruptState, Cer.Success)]
            internal void ResetAsyncState()
            {
                SqlClientEventSource.Log.TryTraceEvent("CachedAsyncState.ResetAsyncState | API | ObjectId {0}, Client Connection Id {1}, AsyncCommandInProgress={2}",
                                                       _cachedAsyncConnection?.ObjectID, _cachedAsyncConnection?.ClientConnectionId, _cachedAsyncConnection?.AsyncCommandInProgress);
                _cachedAsyncCloseCount = -1;
                _cachedAsyncResult = null;
                if (_cachedAsyncConnection != null)
                {
                    _cachedAsyncConnection.AsyncCommandInProgress = false;
                    _cachedAsyncConnection = null;
                }
                _cachedAsyncReader = null;
                _cachedRunBehavior = RunBehavior.ReturnImmediately;
                _cachedSetOptions = null;
                _cachedEndMethod = null;
            }

            internal void SetActiveConnectionAndResult(TaskCompletionSource<object> completion, string endMethod, SqlConnection activeConnection)
            {
                Debug.Assert(activeConnection != null, "Unexpected null connection argument on SetActiveConnectionAndResult!");
                TdsParser parser = activeConnection.Parser;
                SqlClientEventSource.Log.TryTraceEvent("SqlCommand.SetActiveConnectionAndResult | API | ObjectId {0}, Client Connection Id {1}, MARS={2}", activeConnection?.ObjectID, activeConnection?.ClientConnectionId, parser?.MARSOn);
                if ((parser == null) || (parser.State == TdsParserState.Closed) || (parser.State == TdsParserState.Broken))
                {
                    throw ADP.ClosedConnectionError();
                }

                _cachedAsyncCloseCount = activeConnection.CloseCount;
                _cachedAsyncResult = completion;
                if (activeConnection != null && !parser.MARSOn)
                {
                    if (activeConnection.AsyncCommandInProgress)
                        throw SQL.MARSUnsupportedOnConnection();
                }
                _cachedAsyncConnection = activeConnection;

                // Should only be needed for non-MARS, but set anyways.
                _cachedAsyncConnection.AsyncCommandInProgress = true;
                _cachedEndMethod = endMethod;
            }

            internal void SetAsyncReaderState(SqlDataReader ds, RunBehavior runBehavior, string optionSettings)
            {
                _cachedAsyncReader = ds;
                _cachedRunBehavior = runBehavior;
                _cachedSetOptions = optionSettings;
            }
        }

        private AsyncState _cachedAsyncState = null;

        // @TODO: This is never null, so we can remove the null checks from usages of it.
        private AsyncState CachedAsyncState
        {
            get
            {
                _cachedAsyncState ??= new AsyncState();
                return _cachedAsyncState;
            }
        }

        // sql reader will pull this value out for each NextResult call.  It is not cumulative
        // _rowsAffected is cumulative for ExecuteNonQuery across all rpc batches
        internal int _rowsAffected = -1; // rows affected by the command

        // number of rows affected by sp_describe_parameter_encryption.
        // The below line is used only for debug asserts and not exposed publicly or impacts functionality otherwise.
        private int _rowsAffectedBySpDescribeParameterEncryption = -1;

        private SqlNotificationRequest _notification;
        private bool _notificationAutoEnlist = true;            // Notifications auto enlistment is turned on by default

        // transaction support
        private SqlTransaction _transaction;

        private StatementCompletedEventHandler _statementCompletedEventHandler;

        private TdsParserStateObject _stateObj; // this is the TDS session we're using.

        // Volatile bool used to synchronize with cancel thread the state change of an executing
        // command going from pre-processing to obtaining a stateObject.  The cancel synchronization
        // we require in the command is only from entering an Execute* API to obtaining a
        // stateObj.  Once a stateObj is successfully obtained, cancel synchronization is handled
        // by the stateObject.
        private volatile bool _pendingCancel;

        private bool _batchRPCMode;
        private List<_SqlRPC> _RPCList;
        private _SqlRPC[] _sqlRPCParameterEncryptionReqArray;
        private int _currentlyExecutingBatch;
        private SqlRetryLogicBaseProvider _retryLogicProvider;

        /// <summary>
        /// This variable is used to keep track of which RPC batch's results are being read when reading the results of
        /// describe parameter encryption RPC requests in BatchRPCMode.
        /// </summary>
        private int _currentlyExecutingDescribeParameterEncryptionRPC;

        /// <summary>
        /// A flag to indicate if we have in-progress describe parameter encryption RPC requests.
        /// Reset to false when completed.
        /// </summary>
        internal bool IsDescribeParameterEncryptionRPCCurrentlyInProgress { get; private set; }

        /// <summary>
        /// A flag to indicate if EndExecute was already initiated by the Begin call.
        /// </summary>
        private volatile bool _internalEndExecuteInitiated;

        /// <summary>
        /// A flag to indicate whether we postponed caching the query metadata for this command.
        /// </summary>
        internal bool CachingQueryMetadataPostponed { get; set; }

        /// <include file='../../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlCommand.xml' path='docs/members[@name="SqlCommand"]/ctor[@name="default"]/*'/>
        public SqlCommand() : base()
        {
            GC.SuppressFinalize(this);
        }

        /// <include file='../../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlCommand.xml' path='docs/members[@name="SqlCommand"]/ctor[@name="cmdTextString"]/*'/>
        public SqlCommand(string cmdText) : this()
        {
            CommandText = cmdText;
        }

        /// <include file='../../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlCommand.xml' path='docs/members[@name="SqlCommand"]/ctor[@name="cmdTextStringAndSqlConnection"]/*'/>
        public SqlCommand(string cmdText, SqlConnection connection) : this()
        {
            CommandText = cmdText;
            Connection = connection;
        }

        /// <include file='../../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlCommand.xml' path='docs/members[@name="SqlCommand"]/ctor[@name="cmdTextStringAndSqlConnectionAndSqlTransaction"]/*'/>
        public SqlCommand(string cmdText, SqlConnection connection, SqlTransaction transaction) : this()
        {
            CommandText = cmdText;
            Connection = connection;
            Transaction = transaction;
        }

        /// <include file='../../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlCommand.xml' path='docs/members[@name="SqlCommand"]/ctor[@name="cmdTextStringAndSqlConnectionAndSqlTransactionAndSqlCommandColumnEncryptionSetting"]/*'/>
        public SqlCommand(string cmdText, SqlConnection connection, SqlTransaction transaction, SqlCommandColumnEncryptionSetting columnEncryptionSetting) : this()
        {
            CommandText = cmdText;
            Connection = connection;
            Transaction = transaction;
            _columnEncryptionSetting = columnEncryptionSetting;
        }

        private SqlCommand(SqlCommand from) : this()
        {
            CommandText = from.CommandText;
            CommandTimeout = from.CommandTimeout;
            CommandType = from.CommandType;
            Connection = from.Connection;
            DesignTimeVisible = from.DesignTimeVisible;
            Transaction = from.Transaction;
            UpdatedRowSource = from.UpdatedRowSource;
            _columnEncryptionSetting = from.ColumnEncryptionSetting;

            SqlParameterCollection parameters = Parameters;
            foreach (object parameter in from.Parameters)
            {
                parameters.Add((parameter is ICloneable) ? (parameter as ICloneable).Clone() : parameter);
            }
        }

        /// <include file='../../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlCommand.xml' path='docs/members[@name="SqlCommand"]/Connection/*'/>
        [DefaultValue(null)]
        [ResCategory(StringsHelper.ResourceNames.DataCategory_Data)]
        [ResDescription(StringsHelper.ResourceNames.DbCommand_Connection)]
        new public SqlConnection Connection
        {
            get
            {
                return _activeConnection;
            }
            set
            {
                // Don't allow the connection to be changed while in an async operation.
                if (_activeConnection != value && _activeConnection != null)
                {
                    // If new value...
                    if (CachedAsyncState != null && CachedAsyncState.PendingAsyncOperation)
                    {
                        // If in pending async state, throw.
                        throw SQL.CannotModifyPropertyAsyncOperationInProgress();
                    }
                }

                // Check to see if the currently set transaction has completed.  If so,
                // null out our local reference.
                if (_transaction != null && _transaction.Connection == null)
                {
                    _transaction = null;
                }

                // Command is no longer prepared on new connection, cleanup prepare status
                if (IsPrepared)
                {
                    if (_activeConnection != value && _activeConnection != null)
                    {
                        RuntimeHelpers.PrepareConstrainedRegions();
                        try
                        {
                            // cleanup
                            Unprepare();
                        }
                        catch (System.OutOfMemoryException)
                        {
                            _activeConnection.InnerConnection.DoomThisConnection();
                            throw;
                        }
                        catch (System.StackOverflowException)
                        {
                            _activeConnection.InnerConnection.DoomThisConnection();
                            throw;
                        }
                        catch (System.Threading.ThreadAbortException)
                        {
                            _activeConnection.InnerConnection.DoomThisConnection();
                            throw;
                        }
                        catch (Exception)
                        {
                            // we do not really care about errors in unprepare (may be the old connection went bad)
                        }
                        finally
                        {
                            // clean prepare status (even successful Unprepare does not do that)
                            _prepareHandle = -1;
                            _execType = EXECTYPE.UNPREPARED;
                        }
                    }
                }
                _activeConnection = value;
                SqlClientEventSource.Log.TryTraceEvent("<sc.SqlCommand.set_Connection|API> {0}, {1}", ObjectID, value?.ObjectID);
            }
        }

        /// <include file='../../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlCommand.xml' path='docs/members[@name="SqlCommand"]/DbConnection/*'/>
        protected override DbConnection DbConnection
        {
            get
            {
                return Connection;
            }
            set
            {
                Connection = (SqlConnection)value;
            }
        }

        private SqlInternalConnectionTds InternalTdsConnection
        {
            get
            {
                return (SqlInternalConnectionTds)_activeConnection.InnerConnection;
            }
        }

        private bool IsProviderRetriable => SqlConfigurableRetryFactory.IsRetriable(RetryLogicProvider);

        /// <include file='../../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlCommand.xml' path='docs/members[@name="SqlCommand"]/RetryLogicProvider/*' />
        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public SqlRetryLogicBaseProvider RetryLogicProvider
        {
            get
            {
                if (_retryLogicProvider == null)
                {
                    _retryLogicProvider = SqlConfigurableRetryLogicManager.CommandProvider;
                }
                return _retryLogicProvider;
            }
            set
            {
                _retryLogicProvider = value;
            }
        }

        /// <include file='../../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlCommand.xml' path='docs/members[@name="SqlCommand"]/NotificationAutoEnlist/*'/>
        [DefaultValue(true)]
        [ResCategory(StringsHelper.ResourceNames.DataCategory_Notification)]
        [ResDescription(StringsHelper.ResourceNames.SqlCommand_NotificationAutoEnlist)]
        public bool NotificationAutoEnlist
        {
            get
            {
                return _notificationAutoEnlist;
            }
            set
            {
                _notificationAutoEnlist = value;
            }
        }

        /// <include file='../../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlCommand.xml' path='docs/members[@name="SqlCommand"]/Notification/*'/>
        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] // MDAC 90471
        [ResCategory(StringsHelper.ResourceNames.DataCategory_Notification)]
        [ResDescription(StringsHelper.ResourceNames.SqlCommand_Notification)]
        public SqlNotificationRequest Notification
        {
            get
            {
                return _notification;
            }
            set
            {
                SqlClientEventSource.Log.TryTraceEvent("<sc.SqlCommand.set_Notification|API> {0}", ObjectID);
                _sqlDep = null;
                _notification = value;
            }
        }

        internal SqlStatistics Statistics
        {
            get
            {
                if (_activeConnection != null)
                {
                    if (_activeConnection.StatisticsEnabled)
                    {
                        return _activeConnection.Statistics;
                    }
                }
                return null;
            }
        }

        /// <include file='../../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlCommand.xml' path='docs/members[@name="SqlCommand"]/Transaction/*'/>
        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        [ResDescription(StringsHelper.ResourceNames.DbCommand_Transaction)]
        new public SqlTransaction Transaction
        {
            get
            {
                // if the transaction object has been zombied, just return null
                if (_transaction != null && _transaction.Connection == null)
                {
                    _transaction = null;
                }
                return _transaction;
            }
            set
            {
                // Don't allow the transaction to be changed while in an async operation.
                if (_transaction != value && _activeConnection != null)
                {
                    // If new value...
                    if (CachedAsyncState.PendingAsyncOperation)
                    {
                        // If in pending async state, throw
                        throw SQL.CannotModifyPropertyAsyncOperationInProgress();
                    }
                }
                _transaction = value;
                SqlClientEventSource.Log.TryTraceEvent("SqlCommand.Set_Transaction | API | Object Id {0}, Internal Transaction Id {1}, Client Connection Id {2}", ObjectID, value?.InternalTransaction?.TransactionId, Connection?.ClientConnectionId);
            }
        }

        /// <include file='../../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlCommand.xml' path='docs/members[@name="SqlCommand"]/DbTransaction/*'/>
        protected override DbTransaction DbTransaction
        {
            get
            {
                return Transaction;
            }
            set
            {
                Transaction = (SqlTransaction)value;
                SqlClientEventSource.Log.TryTraceEvent("SqlCommand.Set_DbTransaction | API | Object Id {0}, Client Connection Id {1}", ObjectID, Connection?.ClientConnectionId);
            }
        }

        /// <include file='../../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlCommand.xml' path='docs/members[@name="SqlCommand"]/CommandText/*'/>
        [DefaultValue("")]
        [RefreshProperties(RefreshProperties.All)] // MDAC 67707
        [ResCategory(StringsHelper.ResourceNames.DataCategory_Data)]
        [ResDescription(StringsHelper.ResourceNames.DbCommand_CommandText)]
        public override string CommandText
        {
            get => _commandText ?? "";
            set
            {
                if (_commandText != value)
                {
                    PropertyChanging();
                    _commandText = value;
                }
                SqlClientEventSource.Log.TryTraceEvent("SqlCommand.Set_CommandText | API | Object Id {0}, String Value = '{1}', Client Connection Id {2}", ObjectID, value, Connection?.ClientConnectionId);
            }
        }

        /// <include file='../../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlCommand.xml' path='docs/members[@name="SqlCommand"]/ColumnEncryptionSetting/*'/>
        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        [ResCategory(StringsHelper.ResourceNames.DataCategory_Data)]
        [ResDescription(StringsHelper.ResourceNames.TCE_SqlCommand_ColumnEncryptionSetting)]
        public SqlCommandColumnEncryptionSetting ColumnEncryptionSetting => _columnEncryptionSetting;

        /// <include file='../../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlCommand.xml' path='docs/members[@name="SqlCommand"]/CommandTimeout/*'/>
        [ResCategory(StringsHelper.ResourceNames.DataCategory_Data)]
        [ResDescription(StringsHelper.ResourceNames.DbCommand_CommandTimeout)]
        public override int CommandTimeout
        {
            get
            {
                return _commandTimeout ?? DefaultCommandTimeout;
            }
            set
            {
                if (value < 0)
                {
                    throw ADP.InvalidCommandTimeout(value, nameof(CommandTimeout));
                }

                if (value != _commandTimeout)
                {
                    PropertyChanging();
                    _commandTimeout = value;
                }

                SqlClientEventSource.Log.TryTraceEvent("SqlCommand.Set_CommandTimeout | API | ObjectId {0}, Command Timeout value {1}, Client Connection Id {2}", ObjectID, value, Connection?.ClientConnectionId);
            }
        }

        /// <include file='../../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlCommand.xml' path='docs/members[@name="SqlCommand"]/ResetCommandTimeout/*'/>
        public void ResetCommandTimeout()
        {
            if (ADP.DefaultCommandTimeout != CommandTimeout)
            {
                PropertyChanging();
                _commandTimeout = DefaultCommandTimeout;
            }
        }

        private int DefaultCommandTimeout
        {
            get
            {
                return _activeConnection?.CommandTimeout ?? ADP.DefaultCommandTimeout;
            }
        }

        /// <include file='../../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlCommand.xml' path='docs/members[@name="SqlCommand"]/CommandType/*'/>
        [DefaultValue(CommandType.Text)]
        [RefreshProperties(RefreshProperties.All)]
        [ResCategory(StringsHelper.ResourceNames.DataCategory_Data)]
        [ResDescription(StringsHelper.ResourceNames.DbCommand_CommandType)]
        public override CommandType CommandType
        {
            get
            {
                CommandType cmdType = _commandType;
                return ((0 != cmdType) ? cmdType : CommandType.Text);
            }
            set
            {
                if (_commandType != value)
                {
                    switch (value)
                    {
                        case CommandType.Text:
                        case CommandType.StoredProcedure:
                            PropertyChanging();
                            _commandType = value;
                            break;
                        case System.Data.CommandType.TableDirect:
                            throw SQL.NotSupportedCommandType(value);
                        default:
                            throw ADP.InvalidCommandType(value);
                    }

                    SqlClientEventSource.Log.TryTraceEvent("SqlCommand.Set_CommandType | API | ObjectId {0}, Command type value {1}, Client Connection Id {2}", ObjectID, (int)value, Connection?.ClientConnectionId);
                }
            }
        }

        // By default, the cmd object is visible on the design surface (i.e. VS7 Server Tray)
        // to limit the number of components that clutter the design surface,
        // when the DataAdapter design wizard generates the insert/update/delete commands it will
        // set the DesignTimeVisible property to false so that cmds won't appear as individual objects
        /// <include file='../../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlCommand.xml' path='docs/members[@name="SqlCommand"]/DesignTimeVisible/*'/>
        [DefaultValue(true)]
        [DesignOnly(true)]
        [Browsable(false)]
        [EditorBrowsable(EditorBrowsableState.Never)]
        public override bool DesignTimeVisible
        {
            get
            {
                return !_designTimeInvisible;
            }
            set
            {
                _designTimeInvisible = !value;
                TypeDescriptor.Refresh(this);
            }
        }

        /// <include file='../../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlCommand.xml' path='docs/members[@name="SqlCommand"]/EnableOptimizedParameterBinding/*'/>
        public bool EnableOptimizedParameterBinding { get; set; }

        /// <include file='../../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlCommand.xml' path='docs/members[@name="SqlCommand"]/Parameters/*'/>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Content)]
        [ResCategory(StringsHelper.ResourceNames.DataCategory_Data)]
        [ResDescription(StringsHelper.ResourceNames.DbCommand_Parameters)]
        new public SqlParameterCollection Parameters
        {
            get
            {
                if (_parameters == null)
                {
                    // delay the creation of the SqlParameterCollection
                    // until user actually uses the Parameters property
                    _parameters = new SqlParameterCollection();
                }
                return _parameters;
            }
        }

        /// <include file='../../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlCommand.xml' path='docs/members[@name="SqlCommand"]/DbParameterCollection/*'/>
        protected override DbParameterCollection DbParameterCollection
        {
            get
            {
                return Parameters;
            }
        }

        /// <include file='../../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlCommand.xml' path='docs/members[@name="SqlCommand"]/UpdatedRowSource/*'/>
        [DefaultValue(UpdateRowSource.Both)]
        [ResCategory(StringsHelper.ResourceNames.DataCategory_Update)]
        [ResDescription(StringsHelper.ResourceNames.DbCommand_UpdatedRowSource)]
        public override UpdateRowSource UpdatedRowSource
        {
            get
            {
                return _updatedRowSource;
            }
            set
            {
                switch (value)
                {
                    case UpdateRowSource.None:
                    case UpdateRowSource.OutputParameters:
                    case UpdateRowSource.FirstReturnedRecord:
                    case UpdateRowSource.Both:
                        _updatedRowSource = value;
                        break;
                    default:
                        throw ADP.InvalidUpdateRowSource(value);
                }

                SqlClientEventSource.Log.TryTraceEvent("SqlCommand.UpdatedRowSource | API | ObjectId {0}, Updated row source value {1}, Client Connection Id {2}", ObjectID, (int)value, Connection?.ClientConnectionId);
            }
        }

        /// <include file='../../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlCommand.xml' path='docs/members[@name="SqlCommand"]/StatementCompleted/*'/>
        [ResCategory(StringsHelper.ResourceNames.DataCategory_StatementCompleted)]
        [ResDescription(StringsHelper.ResourceNames.DbCommand_StatementCompleted)]
        public event StatementCompletedEventHandler StatementCompleted
        {
            add
            {
                _statementCompletedEventHandler += value;
            }
            remove
            {
                _statementCompletedEventHandler -= value;
            }
        }

        internal void OnStatementCompleted(int recordCount)
        {
            if (0 <= recordCount)
            {
                StatementCompletedEventHandler handler = _statementCompletedEventHandler;
                if (handler != null)
                {
                    try
                    {
                        SqlClientEventSource.Log.TryTraceEvent("SqlCommand.OnStatementCompleted | Info | ObjectId {0}, Record Count {1}, Client Connection Id {2}", ObjectID, recordCount, Connection?.ClientConnectionId);
                        handler(this, new StatementCompletedEventArgs(recordCount));
                    }
                    catch (Exception e)
                    {
                        if (!ADP.IsCatchableOrSecurityExceptionType(e))
                        {
                            throw;
                        }

                        ADP.TraceExceptionWithoutRethrow(e);
                    }
                }
            }
        }

        private void PropertyChanging()
        {
            // also called from SqlParameterCollection
            this.IsDirty = true;
        }

        /// <include file='../../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlCommand.xml' path='docs/members[@name="SqlCommand"]/Prepare/*'/>
        public override void Prepare()
        {
            SqlConnection.ExecutePermission.Demand();

            // Reset _pendingCancel upon entry into any Execute - used to synchronize state
            // between entry into Execute* API and the thread obtaining the stateObject.
            _pendingCancel = false;

            using (TryEventScope.Create("SqlCommand.Prepare | API | Object Id {0}", ObjectID))
            {
                SqlClientEventSource.Log.TryCorrelationTraceEvent("<sc.SqlCommand.Prepare|API|Correlation> ObjectID {0}, ActivityID {1}", ObjectID, ActivityCorrelator.Current);

                SqlStatistics statistics = SqlStatistics.StartTimer(Statistics);

                // only prepare if batch with parameters
                // MDAC BUG #'s 73776 & 72101
                if (
                    IsPrepared && !IsDirty
                    || CommandType == CommandType.StoredProcedure
                    || (CommandType == CommandType.Text && GetParameterCount(_parameters) == 0)
                )
                {
                    if (Statistics != null)
                    {
                        Statistics.SafeIncrement(ref Statistics._prepares);
                    }
                    _hiddenPrepare = false;
                }
                else
                {
                    // Validate the command outside of the try\catch to avoid putting the _stateObj on error
                    ValidateCommand(isAsync: false);

                    bool processFinallyBlock = true;
                    TdsParser bestEffortCleanupTarget = null;
                    RuntimeHelpers.PrepareConstrainedRegions();
                    try
                    {
                        bestEffortCleanupTarget = SqlInternalConnection.GetBestEffortCleanupTarget(_activeConnection);

                        // NOTE: The state object isn't actually needed for this, but it is still here for back-compat (since it does a bunch of checks)
                        GetStateObject();

                        // Loop through parameters ensuring that we do not have unspecified types, sizes, scales, or precisions
                        if (_parameters != null)
                        {
                            int count = _parameters.Count;
                            for (int i = 0; i < count; ++i)
                            {
                                _parameters[i].Prepare(this); // MDAC 67063
                            }
                        }

                        InternalPrepare();
                    }
                    catch (System.OutOfMemoryException e)
                    {
                        processFinallyBlock = false;
                        _activeConnection.Abort(e);
                        throw;
                    }
                    catch (System.StackOverflowException e)
                    {
                        processFinallyBlock = false;
                        _activeConnection.Abort(e);
                        throw;
                    }
                    catch (System.Threading.ThreadAbortException e)
                    {
                        processFinallyBlock = false;
                        _activeConnection.Abort(e);

                        SqlInternalConnection.BestEffortCleanup(bestEffortCleanupTarget);
                        throw;
                    }
                    catch (Exception e)
                    {
                        processFinallyBlock = ADP.IsCatchableExceptionType(e);
                        throw;
                    }
                    finally
                    {
                        if (processFinallyBlock)
                        {
                            _hiddenPrepare = false; // The command is now officially prepared

                            ReliablePutStateObject();
                        }
                    }
                }

                SqlStatistics.StopTimer(statistics);
            }
        }

        private void InternalPrepare()
        {
            if (this.IsDirty)
            {
                Debug.Assert(_cachedMetaData == null || !_dirty, "dirty query should not have cached metadata!"); // can have cached metadata if dirty because of parameters
                //
                // someone changed the command text or the parameter schema so we must unprepare the command
                //
                this.Unprepare();
                this.IsDirty = false;
            }
            Debug.Assert(_execType != EXECTYPE.PREPARED, "Invalid attempt to Prepare already Prepared command!");
            Debug.Assert(_activeConnection != null, "must have an open connection to Prepare");
            Debug.Assert(_stateObj != null, "TdsParserStateObject should not be null");
            Debug.Assert(_stateObj.Parser != null, "TdsParser class should not be null in Command.Execute!");
            Debug.Assert(_stateObj.Parser == _activeConnection.Parser, "stateobject parser not same as connection parser");
            Debug.Assert(false == _inPrepare, "Already in Prepare cycle, this.inPrepare should be false!");

            // remember that the user wants to do a prepare but don't actually do an rpc
            _execType = EXECTYPE.PREPAREPENDING;
            // Note the current close count of the connection - this will tell us if the connection has been closed between calls to Prepare() and Execute
            _preparedConnectionCloseCount = _activeConnection.CloseCount;
            _preparedConnectionReconnectCount = _activeConnection.ReconnectCount;

            if (Statistics != null)
            {
                Statistics.SafeIncrement(ref Statistics._prepares);
            }
        }

        // SqlInternalConnectionTds needs to be able to unprepare a statement
        internal void Unprepare()
        {
            Debug.Assert(true == IsPrepared, "Invalid attempt to Unprepare a non-prepared command!");
            Debug.Assert(_activeConnection != null, "must have an open connection to UnPrepare");
            Debug.Assert(false == _inPrepare, "_inPrepare should be false!");
            _execType = EXECTYPE.PREPAREPENDING;

            SqlClientEventSource.Log.TryTraceEvent("SqlCommand.UnPrepare | Info | Object Id {0}, Current Prepared Handle {1}", ObjectID, _prepareHandle);

            // Don't zero out the handle because we'll pass it in to sp_prepexec on the next prepare
            // Unless the close count isn't the same as when we last prepared
            if ((_activeConnection.CloseCount != _preparedConnectionCloseCount) || (_activeConnection.ReconnectCount != _preparedConnectionReconnectCount))
            {
                // reset our handle
                _prepareHandle = -1;
            }

            _cachedMetaData = null;
            SqlClientEventSource.Log.TryTraceEvent("SqlCommand.UnPrepare | Info | Object Id {0}, Command unprepared.", ObjectID);
        }

        // Cancel is supposed to be multi-thread safe.
        // It doesn't make sense to verify the connection exists or that it is open during cancel
        // because immediately after checkin the connection can be closed or removed via another thread.
        //
        /// <include file='../../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlCommand.xml' path='docs/members[@name="SqlCommand"]/Cancel/*'/>
        public override void Cancel()
        {
            using (TryEventScope.Create("SqlCommand.Cancel | API | Object Id {0}", ObjectID))
            {
                SqlClientEventSource.Log.TryCorrelationTraceEvent("SqlCommand.Cancel | API | Correlation | Object Id {0}, Activity Id {1}, Client Connection Id {2}, Command Text '{3}'", ObjectID, ActivityCorrelator.Current, Connection?.ClientConnectionId, CommandText);

                SqlStatistics statistics = null;
                try
                {
                    statistics = SqlStatistics.StartTimer(Statistics);

                    // If we are in reconnect phase simply cancel the waiting task
                    var reconnectCompletionSource = _reconnectionCompletionSource;
                    if (reconnectCompletionSource != null)
                    {
                        if (reconnectCompletionSource.TrySetCanceled())
                        {
                            return;
                        }
                    }

                    // the pending data flag means that we are awaiting a response or are in the middle of processing a response
                    // if we have no pending data, then there is nothing to cancel
                    // if we have pending data, but it is not a result of this command, then we don't cancel either.  Note that
                    // this model is implementable because we only allow one active command at any one time.  This code
                    // will have to change we allow multiple outstanding batches
                    if (_activeConnection == null)
                    {
                        return;
                    }
                    SqlInternalConnectionTds connection = (_activeConnection.InnerConnection as SqlInternalConnectionTds);
                    if (connection == null)
                    {  // Fail with out locking
                        return;
                    }

                    // The lock here is to protect against the command.cancel / connection.close race condition
                    // The SqlInternalConnectionTds is set to OpenBusy during close, once this happens the cast below will fail and
                    // the command will no longer be cancelable.  It might be desirable to be able to cancel the close operation, but this is
                    // outside of the scope of Whidbey RTM.  See (SqlConnection::Close) for other lock.
                    lock (connection)
                    {
                        if (connection != (_activeConnection.InnerConnection as SqlInternalConnectionTds))
                        {
                            // make sure the connection held on the active connection is what we have stored in our temp connection variable, if not between getting "connection" and taking the lock, the connection has been closed
                            return;
                        }

                        TdsParser parser = connection.Parser;
                        if (parser == null)
                        {
                            return;
                        }

                        TdsParser bestEffortCleanupTarget = null;
                        RuntimeHelpers.PrepareConstrainedRegions();
                        try
                        {
                            bestEffortCleanupTarget = SqlInternalConnection.GetBestEffortCleanupTarget(_activeConnection);

                            if (!_pendingCancel)
                            {
                                // Do nothing if aleady pending.
                              // Before attempting actual cancel, set the _pendingCancel flag to false.
                              // This denotes to other thread before obtaining stateObject from the
                              // session pool that there is another thread wishing to cancel.
                              // The period in question is between entering the ExecuteAPI and obtaining
                              // a stateObject.
                                _pendingCancel = true;

                                TdsParserStateObject stateObj = _stateObj;
                                if (stateObj != null)
                                {
                                    stateObj.Cancel(this);
                                }
                                else
                                {
                                    SqlDataReader reader = connection.FindLiveReader(this);
                                    if (reader != null)
                                    {
                                        reader.Cancel(this);
                                    }
                                }
                            }
                        }
                        catch (System.OutOfMemoryException e)
                        {
                            _activeConnection.Abort(e);
                            throw;
                        }
                        catch (System.StackOverflowException e)
                        {
                            _activeConnection.Abort(e);
                            throw;
                        }
                        catch (System.Threading.ThreadAbortException e)
                        {
                            _activeConnection.Abort(e);
                            SqlInternalConnection.BestEffortCleanup(bestEffortCleanupTarget);
                            throw;
                        }
                    }
                }
                finally
                {
                    SqlStatistics.StopTimer(statistics);
                }
            }
        }

        /// <include file='../../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlCommand.xml' path='docs/members[@name="SqlCommand"]/CreateParameter/*'/>
        new public SqlParameter CreateParameter()
        {
            return new SqlParameter();
        }

        /// <include file='../../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlCommand.xml' path='docs/members[@name="SqlCommand"]/CreateDbParameter/*'/>
        protected override DbParameter CreateDbParameter()
        {
            return CreateParameter();
        }

        /// <include file='../../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlCommand.xml' path='docs/members[@name="SqlCommand"]/Dispose/*'/>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // release managed objects
                _cachedMetaData = null;

                // reset async cache information to allow a second async execute
                CachedAsyncState?.ResetAsyncState();
            }
            // release unmanaged objects
            base.Dispose(disposing);
        }

        private SqlDataReader RunExecuteReaderWithRetry(
            CommandBehavior cmdBehavior,
            RunBehavior runBehavior,
            bool returnStream,
            [CallerMemberName] string method = "")
        {
            return RetryLogicProvider.Execute(
                this,
                () => RunExecuteReader(cmdBehavior, runBehavior, returnStream, method));
        }

        /// <include file='../../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlCommand.xml' path='docs/members[@name="SqlCommand"]/ExecuteScalar/*'/>
        public override object ExecuteScalar()
        {
            SqlConnection.ExecutePermission.Demand();

            // Reset _pendingCancel upon entry into any Execute - used to synchronize state
            // between entry into Execute* API and the thread obtaining the stateObject.
            _pendingCancel = false;
            SqlStatistics statistics = null;

            using (TryEventScope.Create("SqlCommand.ExecuteScalar | API | ObjectId {0}", ObjectID))
            {
                bool success = false;
                int? sqlExceptionNumber = null;
                SqlClientEventSource.Log.TryCorrelationTraceEvent("SqlCommand.ExecuteScalar | API | Correlation | Object Id {0}, Activity Id {1}, Client Connection Id {2}, Command Text '{3}'", ObjectID, ActivityCorrelator.Current, Connection?.ClientConnectionId, CommandText);

                try
                {
                    statistics = SqlStatistics.StartTimer(Statistics);
                    WriteBeginExecuteEvent();
                    SqlDataReader ds = IsProviderRetriable
                        ? RunExecuteReaderWithRetry(0, RunBehavior.ReturnImmediately, returnStream: true)
                        : RunExecuteReader(0, RunBehavior.ReturnImmediately, returnStream: true);
                    object result = CompleteExecuteScalar(ds, _batchRPCMode);
                    success = true;
                    return result;
                }
                catch (SqlException ex)
                {
                    sqlExceptionNumber = ex.Number;
                    throw;
                }
                finally
                {
                    SqlStatistics.StopTimer(statistics);
                    WriteEndExecuteEvent(success, sqlExceptionNumber, synchronous: true);
                }
            }
        }

        private object CompleteExecuteScalar(SqlDataReader ds, bool returnLastResult)
        {
            object retResult = null;

            try
            {
                do
                {
                    if (ds.Read())
                    {
                        if (ds.FieldCount > 0)
                        {
                            retResult = ds.GetValue(0);
                        }
                    }
                } while (returnLastResult && ds.NextResult());
            }
            finally
            {
                // clean off the wire
                ds.Close();
            }

            return retResult;
        }

        private Task InternalExecuteNonQueryWithRetry(
            bool sendToPipe,
            int timeout,
            out bool usedCache,
            bool asyncWrite,
            bool isRetry,
            [CallerMemberName] string methodName = "")
        {
            bool innerUsedCache = false;
            Task result = RetryLogicProvider.Execute(
                this,
                () => InternalExecuteNonQuery(
                    completion: null,
                    sendToPipe,
                    timeout,
                    out innerUsedCache,
                    asyncWrite,
                    isRetry,
                    methodName));
            usedCache = innerUsedCache;
            return result;
        }

        /// <include file='../../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlCommand.xml' path='docs/members[@name="SqlCommand"]/ExecuteNonQuery[@name="default"]/*'/>
        public override int ExecuteNonQuery()
        {
            SqlConnection.ExecutePermission.Demand();

            // Reset _pendingCancel upon entry into any Execute - used to synchronize state
            // between entry into Execute* API and the thread obtaining the stateObject.
            _pendingCancel = false;

            SqlStatistics statistics = null;

            using (TryEventScope.Create("<sc.SqlCommand.ExecuteNonQuery|API> {0}", ObjectID))
            {
                bool success = false;
                int? sqlExceptionNumber = null;
                SqlClientEventSource.Log.TryCorrelationTraceEvent("SqlCommand.ExecuteNonQuery | API | Correlation | Object Id {0}, ActivityID {1}, Client Connection Id {2}, Command Text {3}", ObjectID, ActivityCorrelator.Current, Connection?.ClientConnectionId, CommandText);

                try
                {
                    statistics = SqlStatistics.StartTimer(Statistics);
                    WriteBeginExecuteEvent();
                    if (IsProviderRetriable)
                    {
                        InternalExecuteNonQueryWithRetry(
                            sendToPipe: false,
                            CommandTimeout,
                            out _,
                            asyncWrite: false,
                            isRetry: false);
                    }
                    else
                    {
                        InternalExecuteNonQuery(completion: null, sendToPipe: false, CommandTimeout, out _);
                    }
                    success = true;
                    return _rowsAffected;
                }
                catch (SqlException ex)
                {
                    sqlExceptionNumber = ex.Number;
                    throw;
                }
                finally
                {
                    SqlStatistics.StopTimer(statistics);
                    WriteEndExecuteEvent(success, sqlExceptionNumber, synchronous: true);
                }
            }
        }

        /// <include file='../../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlCommand.xml' path='docs/members[@name="SqlCommand"]/BeginExecuteNonQuery[@name="default"]/*'/>
        [System.Security.Permissions.HostProtectionAttribute(ExternalThreading = true)]
        public IAsyncResult BeginExecuteNonQuery() =>
            BeginExecuteNonQuery(null, null);

        /// <include file='../../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlCommand.xml' path='docs/members[@name="SqlCommand"]/BeginExecuteNonQuery[@name="AsyncCallbackAndStateObject"]/*'/>
        [System.Security.Permissions.HostProtectionAttribute(ExternalThreading = true)]
        public IAsyncResult BeginExecuteNonQuery(AsyncCallback callback, object stateObject)
        {
            SqlClientEventSource.Log.TryCorrelationTraceEvent("<sc.SqlCommand.BeginExecuteNonQuery|API|Correlation> ObjectID {0}, ActivityID {1}", ObjectID, ActivityCorrelator.Current);
            SqlConnection.ExecutePermission.Demand();
            return BeginExecuteNonQueryInternal(0, callback, stateObject, 0, isRetry: false);
        }

        private IAsyncResult BeginExecuteNonQueryAsync(AsyncCallback callback, object stateObject)
        {
            SqlClientEventSource.Log.TryCorrelationTraceEvent("SqlCommand.BeginExecuteNonQueryAsync | API | Correlation | Object Id {0}, Activity Id {1}, Client Connection Id {2}, Command Text '{3}'", ObjectID, ActivityCorrelator.Current, Connection?.ClientConnectionId, CommandText);
            return BeginExecuteNonQueryInternal(0, callback, stateObject, CommandTimeout, isRetry: false, asyncWrite: true);
        }

        private IAsyncResult BeginExecuteNonQueryInternal(CommandBehavior behavior, AsyncCallback callback, object stateObject, int timeout, bool isRetry, bool asyncWrite = false)
        {
            TaskCompletionSource<object> globalCompletion = new TaskCompletionSource<object>(stateObject);
            TaskCompletionSource<object> localCompletion = new TaskCompletionSource<object>(stateObject);

            if (!isRetry)
            {
                // Reset _pendingCancel upon entry into any Execute - used to synchronize state
                // between entry into Execute* API and the thread obtaining the stateObject.
                _pendingCancel = false;

                ValidateAsyncCommand(); // Special case - done outside of try/catches to prevent putting a stateObj
                                        // back into pool when we should not.
            }

            SqlStatistics statistics = null;
            try
            {
                if (!isRetry)
                {
                    statistics = SqlStatistics.StartTimer(Statistics);
                    WriteBeginExecuteEvent();
                }

                bool usedCache;
                try
                {
                    // InternalExecuteNonQuery already has reliability block, but if failure will not put stateObj back into pool.
                    Task execNQ = InternalExecuteNonQuery(
                        localCompletion,
                        sendToPipe: false,
                        timeout,
                        out usedCache,
                        asyncWrite,
                        isRetry,
                        methodName: nameof(BeginExecuteNonQuery));

                    if (execNQ != null)
                    {
                        AsyncHelper.ContinueTaskWithState(execNQ, localCompletion, this, (object state) => ((SqlCommand)state).BeginExecuteNonQueryInternalReadStage(localCompletion));
                    }
                    else
                    {
                        BeginExecuteNonQueryInternalReadStage(localCompletion);
                    }
                }
                catch (Exception e)
                {
                    if (!ADP.IsCatchableOrSecurityExceptionType(e))
                    {
                        // If not catchable - the connection has already been caught and doomed in RunExecuteReader.
                        throw;
                    }

                    // For async, RunExecuteReader will never put the stateObj back into the pool, so do so now.
                    ReliablePutStateObject();
                    throw;
                }

                // When we use query caching for parameter encryption we need to retry on specific errors.
                // In these cases finalize the call internally and trigger a retry when needed.
                if (
                    !TriggerInternalEndAndRetryIfNecessary(
                        behavior,
                        stateObject,
                        timeout,
                        usedCache,
                        isRetry,
                        asyncWrite,
                        globalCompletion,
                        localCompletion,
                        endFunc: static (SqlCommand command, IAsyncResult asyncResult, bool isInternal, string endMethod) =>
                        {
                            return command.InternalEndExecuteNonQuery(asyncResult, isInternal, endMethod);
                        },
                        retryFunc: static (SqlCommand command, CommandBehavior commandBehavior, AsyncCallback callback, object stateObject, int timeout, bool isRetry, bool asyncWrite) =>
                        {
                            return command.BeginExecuteNonQueryInternal(commandBehavior, callback, stateObject, timeout, isRetry, asyncWrite);
                        },
                        nameof(EndExecuteNonQuery)))
                {
                    globalCompletion = localCompletion;
                }

                // Add callback after work is done to avoid overlapping Begin/End methods
                if (callback != null)
                {
                    globalCompletion.Task.ContinueWith((t) => callback(t), TaskScheduler.Default);
                }

                return globalCompletion.Task;
            }
            finally
            {
                SqlStatistics.StopTimer(statistics);
            }
        }

        private void BeginExecuteNonQueryInternalReadStage(TaskCompletionSource<object> completion)
        {
            // Read SNI does not have catches for async exceptions, handle here.
            TdsParser bestEffortCleanupTarget = null;
            RuntimeHelpers.PrepareConstrainedRegions();
            try
            {
                bestEffortCleanupTarget = SqlInternalConnection.GetBestEffortCleanupTarget(_activeConnection);
                // must finish caching information before ReadSni which can activate the callback before returning
                CachedAsyncState.SetActiveConnectionAndResult(completion, nameof(EndExecuteNonQuery), _activeConnection);
                _stateObj.ReadSni(completion);
            }
            catch (System.OutOfMemoryException e)
            {
                _activeConnection.Abort(e);
                throw;
            }
            catch (System.StackOverflowException e)
            {
                _activeConnection.Abort(e);
                throw;
            }
            catch (System.Threading.ThreadAbortException e)
            {
                _activeConnection.Abort(e);
                SqlInternalConnection.BestEffortCleanup(bestEffortCleanupTarget);
                throw;
            }
            catch (Exception)
            {
                // Similarly, if an exception occurs put the stateObj back into the pool.
                // and reset async cache information to allow a second async execute
                if (CachedAsyncState != null)
                {
                    CachedAsyncState.ResetAsyncState();
                }
                ReliablePutStateObject();
                throw;
            }
        }

        private void VerifyEndExecuteState(Task completionTask, string endMethod, bool fullCheckForColumnEncryption = false)
        {
            Debug.Assert(completionTask != null);
            SqlClientEventSource.Log.TryTraceEvent("SqlCommand.VerifyEndExecuteState | API | ObjectId {0}, Client Connection Id {1}, MARS={2}, AsyncCommandInProgress={3}",
                                                    _activeConnection?.ObjectID, _activeConnection?.ClientConnectionId,
                                                    _activeConnection?.Parser?.MARSOn, _activeConnection?.AsyncCommandInProgress);

            if (completionTask.IsCanceled)
            {
                if (_stateObj != null)
                {
                    _stateObj.Parser.State = TdsParserState.Broken; // We failed to respond to attention, we have to quit!
                    _stateObj.Parser.Connection.BreakConnection();
                    _stateObj.Parser.ThrowExceptionAndWarning(_stateObj, this);
                }
                else
                {
                    Debug.Assert(_reconnectionCompletionSource == null || _reconnectionCompletionSource.Task.IsCanceled, "ReconnectCompletionSource should be null or cancelled");
                    throw SQL.CR_ReconnectionCancelled();
                }
            }
            else if (completionTask.IsFaulted)
            {
                throw completionTask.Exception.InnerException;
            }

            // If transparent parameter encryption was attempted, then we need to skip other checks like those on EndMethodName
            // since we want to wait for async results before checking those fields.
            if (IsColumnEncryptionEnabled && !fullCheckForColumnEncryption)
            {
                if (_activeConnection.State != ConnectionState.Open)
                {
                    // If the connection is not 'valid' then it was closed while we were executing
                    throw ADP.ClosedConnectionError();
                }

                return;
            }

            if (CachedAsyncState.EndMethodName == null)
            {
                throw ADP.MethodCalledTwice(endMethod);
            }
            if (endMethod != CachedAsyncState.EndMethodName)
            {
                throw ADP.MismatchedAsyncResult(CachedAsyncState.EndMethodName, endMethod);
            }
            if ((_activeConnection.State != ConnectionState.Open) || (!CachedAsyncState.IsActiveConnectionValid(_activeConnection)))
            {
                // If the connection is not 'valid' then it was closed while we were executing
                throw ADP.ClosedConnectionError();
            }
        }

        private void WaitForAsyncResults(IAsyncResult asyncResult, bool isInternal)
        {
            Task completionTask = (Task)asyncResult;
            if (!asyncResult.IsCompleted)
            {
                asyncResult.AsyncWaitHandle.WaitOne();
            }

            if (_stateObj != null)
            {
                _stateObj._networkPacketTaskSource = null;
            }

            // If this is an internal command we will decrement the count when the End method is actually called by the user.
            // If we are using Column Encryption and the previous task failed, the async count should have already been fixed up.
            // There is a generic issue in how we handle the async count because:
            // a) BeginExecute might or might not clean it up on failure.
            // b) In EndExecute, we check the task state before waiting and throw if it's failed, whereas if we wait we will always adjust the count.
            if (!isInternal && (!IsColumnEncryptionEnabled || !completionTask.IsFaulted))
            {
                _activeConnection.GetOpenTdsConnection().DecrementAsyncCount();
            }
        }

        /// <include file='../../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlCommand.xml' path='docs/members[@name="SqlCommand"]/EndExecuteNonQuery[@name="IAsyncResult"]/*'/>
        public int EndExecuteNonQuery(IAsyncResult asyncResult)
        {
            try
            {
                return EndExecuteNonQueryInternal(asyncResult);
            }
            finally
            {
                SqlClientEventSource.Log.TryCorrelationTraceEvent("SqlCommand.EndExecuteNonQuery | API | Correlation | Object Id {0}, Activity Id {1}, Client Connection Id {2}, Command Text '{3}'", ObjectID, ActivityCorrelator.Current, Connection?.ClientConnectionId, CommandText);
            }
        }

        private void ThrowIfReconnectionHasBeenCanceled()
        {
            if (_stateObj == null)
            {
                var reconnectionCompletionSource = _reconnectionCompletionSource;
                if (reconnectionCompletionSource != null && reconnectionCompletionSource.Task.IsCanceled)
                {
                    throw SQL.CR_ReconnectionCancelled();
                }
            }
        }

        private int EndExecuteNonQueryAsync(IAsyncResult asyncResult)
        {
            SqlClientEventSource.Log.TryCorrelationTraceEvent("SqlCommand.EndExecuteNonQueryAsync | Info | Correlation | Object Id {0}, Activity Id {1}, Client Connection Id {2}, Command Text '{3}'", ObjectID, ActivityCorrelator.Current, Connection?.ClientConnectionId, CommandText);
            Debug.Assert(!_internalEndExecuteInitiated || _stateObj == null);

            Exception asyncException = ((Task)asyncResult).Exception;
            if (asyncException != null)
            {
                // Leftover exception from the Begin...InternalReadStage
                CachedAsyncState?.ResetAsyncState();
                ReliablePutStateObject();
                throw asyncException.InnerException;
            }
            else
            {
                ThrowIfReconnectionHasBeenCanceled();
                // lock on _stateObj prevents races with close/cancel.
                // If we have already initiate the End call internally, we have already done that, so no point doing it again.
                if (!_internalEndExecuteInitiated)
                {
                    lock (_stateObj)
                    {
                        return EndExecuteNonQueryInternal(asyncResult);
                    }
                }
                else
                {
                    return EndExecuteNonQueryInternal(asyncResult);
                }
            }
        }

        private int EndExecuteNonQueryInternal(IAsyncResult asyncResult)
        {
            SqlStatistics statistics = null;
            int? sqlExceptionNumber = null;
            bool success = false;

            try
            {
                statistics = SqlStatistics.StartTimer(Statistics);
                int result = (int)InternalEndExecuteNonQuery(asyncResult, isInternal: false, endMethod: nameof(EndExecuteNonQuery));
                success = true;
                return result;
            }
            catch (SqlException e)
            {
                sqlExceptionNumber = e.Number;
                CachedAsyncState?.ResetAsyncState();

                //  SqlException is always catchable
                ReliablePutStateObject();
                throw;
            }
            catch (Exception e)
            {
                CachedAsyncState?.ResetAsyncState();
                if (ADP.IsCatchableExceptionType(e))
                {
                    ReliablePutStateObject();
                };
                throw;
            }
            finally
            {
                SqlStatistics.StopTimer(statistics);
                WriteEndExecuteEvent(success, sqlExceptionNumber, synchronous: false);
            }
        }

        private object InternalEndExecuteNonQuery(
            IAsyncResult asyncResult,
            bool isInternal,
            [CallerMemberName] string endMethod = "")
        {
            SqlClientEventSource.Log.TryTraceEvent("SqlCommand.InternalEndExecuteNonQuery | INFO | ObjectId {0}, Client Connection Id {1}, MARS={2}, AsyncCommandInProgress={3}",
                                                    _activeConnection?.ObjectID, _activeConnection?.ClientConnectionId,
                                                    _activeConnection?.Parser?.MARSOn, _activeConnection?.AsyncCommandInProgress);
            TdsParser bestEffortCleanupTarget = null;
            RuntimeHelpers.PrepareConstrainedRegions();

            try
            {
                bestEffortCleanupTarget = SqlInternalConnection.GetBestEffortCleanupTarget(_activeConnection);
                VerifyEndExecuteState((Task)asyncResult, endMethod);
                WaitForAsyncResults(asyncResult, isInternal);

                // If column encryption is enabled, also check the state after waiting for the task.
                // It would be better to do this for all cases, but avoiding for compatibility reasons.
                if (IsColumnEncryptionEnabled)
                {
                    VerifyEndExecuteState((Task)asyncResult, endMethod, fullCheckForColumnEncryption: true);
                }

                bool processFinallyBlock = true;
                try
                {
                    // If this is not for internal usage, notify the dependency.
                    // If we have already initiated the end internally, the reader should be ready, so just return the rows affected.
                    if (!isInternal)
                    {
                        NotifyDependency();

                        if (_internalEndExecuteInitiated)
                        {
                            Debug.Assert(_stateObj == null);

                            // Reset the state since we exit early.
                            CachedAsyncState.ResetAsyncState();

                            return _rowsAffected;
                        }
                    }

                    CheckThrowSNIException();

                    // only send over SQL Batch command if we are not a stored proc and have no parameters
                    if ((System.Data.CommandType.Text == this.CommandType) && (0 == GetParameterCount(_parameters)))
                    {
                        try
                        {
                            Debug.Assert(_stateObj._syncOverAsync, "Should not attempt pends in a synchronous call");
                            TdsOperationStatus result = _stateObj.Parser.TryRun(RunBehavior.UntilDone, this, null, null, _stateObj, out _);
                            if (result != TdsOperationStatus.Done)
                            {
                                throw SQL.SynchronousCallMayNotPend();
                            }
                        }
                        finally
                        {
                            // Don't reset the state for internal End. The user End will do that eventually.
                            if (!isInternal)
                            {
                                CachedAsyncState.ResetAsyncState();
                            }
                        }
                    }
                    else
                    {
                        // otherwise, use a full-fledged execute that can handle params and stored procs
                        SqlDataReader reader = CompleteAsyncExecuteReader(isInternal);
                        if (reader != null)
                        {
                            reader.Close();
                        }
                    }
                }
                catch (Exception e)
                {
                    processFinallyBlock = ADP.IsCatchableExceptionType(e);
                    throw;
                }
                finally
                {
                    if (processFinallyBlock)
                    {
                        PutStateObject();
                    }
                }

                Debug.Assert(_stateObj == null, "non-null state object in EndExecuteNonQuery");
                return _rowsAffected;
            }
            catch (System.OutOfMemoryException e)
            {
                _activeConnection.Abort(e);
                throw;
            }
            catch (System.StackOverflowException e)
            {
                _activeConnection.Abort(e);
                throw;
            }
            catch (System.Threading.ThreadAbortException e)
            {
                _activeConnection.Abort(e);
                SqlInternalConnection.BestEffortCleanup(bestEffortCleanupTarget);
                throw;
            }
        }

        private Task InternalExecuteNonQuery(
            TaskCompletionSource<object> completion,
            bool sendToPipe,
            int timeout,
            out bool usedCache,
            bool asyncWrite = false,
            bool isRetry = false,
            [CallerMemberName] string methodName = "")
        {
            SqlClientEventSource.Log.TryTraceEvent("SqlCommand.InternalExecuteNonQuery | INFO | ObjectId {0}, Client Connection Id {1}, AsyncCommandInProgress={2}",
                                                    _activeConnection?.ObjectID, _activeConnection?.ClientConnectionId, _activeConnection?.AsyncCommandInProgress);
            bool isAsync = completion != null;
            usedCache = false;

            SqlStatistics statistics = Statistics;
            _rowsAffected = -1;

            TdsParser bestEffortCleanupTarget = null;
            RuntimeHelpers.PrepareConstrainedRegions();
            try
            {
                bestEffortCleanupTarget = SqlInternalConnection.GetBestEffortCleanupTarget(_activeConnection);
                // @devnote: this function may throw for an invalid connection
                // @devnote: returns false for empty command text
                if (!isRetry)
                {
                    ValidateCommand(isAsync, methodName);
                }
                CheckNotificationStateAndAutoEnlist(); // Only call after validate - requires non null connection!

                Task task = null;

                // Always Encrypted generally operates only on parameterized queries. However enclave based Always encrypted also supports unparameterized queries
                // We skip this block for enclave based always encrypted so that we can make a call to SQL Server to get the encryption information
                if (!ShouldUseEnclaveBasedWorkflow && !_batchRPCMode && CommandType == CommandType.Text && GetParameterCount(_parameters) == 0)
                {
                    Debug.Assert(!sendToPipe, "trying to send non-context command to pipe");
                    if (statistics != null)
                    {
                        if (!IsDirty && IsPrepared)
                        {
                            statistics.SafeIncrement(ref statistics._preparedExecs);
                        }
                        else
                        {
                            statistics.SafeIncrement(ref statistics._unpreparedExecs);
                        }
                    }

                    // We should never get here for a retry since we only have retries for parameters.
                    Debug.Assert(!isRetry);

                    task = RunExecuteNonQueryTds(methodName, isAsync, timeout, asyncWrite);
                }
                else
                {
                    // otherwise, use a full-fledged execute that can handle params and stored procs
                    Debug.Assert(!sendToPipe, "Trying to send non-context command to pipe");
                    SqlClientEventSource.Log.TryTraceEvent("SqlCommand.InternalExecuteNonQuery | INFO | Object Id {0}, RPC execute method name {1}, isAsync {2}, isRetry {3}", ObjectID, methodName, isAsync, isRetry);

                    SqlDataReader reader = RunExecuteReader(
                        CommandBehavior.Default,
                        RunBehavior.UntilDone,
                        returnStream: false,
                        completion,
                        timeout,
                        out task,
                        out usedCache,
                        asyncWrite,
                        isRetry,
                        methodName);
                    
                    if (reader != null)
                    {
                        if (task != null)
                        {
                            task = AsyncHelper.CreateContinuationTask(task, () => reader.Close());
                        }
                        else
                        {
                            reader.Close();
                        }
                    }
                }
                Debug.Assert(isAsync || _stateObj == null, "non-null state object in InternalExecuteNonQuery");
                return task;
            }
            catch (System.OutOfMemoryException e)
            {
                _activeConnection.Abort(e);
                throw;
            }
            catch (System.StackOverflowException e)
            {
                _activeConnection.Abort(e);
                throw;
            }
            catch (System.Threading.ThreadAbortException e)
            {
                _activeConnection.Abort(e);
                SqlInternalConnection.BestEffortCleanup(bestEffortCleanupTarget);
                throw;
            }
        }

        /// <include file='../../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlCommand.xml' path='docs/members[@name="SqlCommand"]/ExecuteXmlReader/*'/>
        public XmlReader ExecuteXmlReader()
        {
            SqlConnection.ExecutePermission.Demand();

            // Reset _pendingCancel upon entry into any Execute - used to synchronize state
            // between entry into Execute* API and the thread obtaining the stateObject.
            _pendingCancel = false;

            SqlStatistics statistics = null;

            using (TryEventScope.Create("SqlCommand.ExecuteXmlReader | API | Object Id {0}", ObjectID))
            {
                bool success = false;
                int? sqlExceptionNumber = null;
                SqlClientEventSource.Log.TryCorrelationTraceEvent("SqlCommand.ExecuteXmlReader | API | Correlation | Object Id {0}, Activity Id {1}, Client Connection Id {2}, Command Text '{3}'", ObjectID, ActivityCorrelator.Current, Connection?.ClientConnectionId, CommandText);

                try
                {
                    statistics = SqlStatistics.StartTimer(Statistics);
                    WriteBeginExecuteEvent();

                    // use the reader to consume metadata
                    SqlDataReader ds = IsProviderRetriable
                        ? RunExecuteReaderWithRetry(CommandBehavior.SequentialAccess, RunBehavior.ReturnImmediately, returnStream: true)
                        : RunExecuteReader(CommandBehavior.SequentialAccess, RunBehavior.ReturnImmediately, returnStream: true);
                    XmlReader result = CompleteXmlReader(ds);
                    success = true;
                    return result;
                }
                catch (SqlException ex)
                {
                    sqlExceptionNumber = ex.Number;
                    throw;
                }
                finally
                {
                    SqlStatistics.StopTimer(statistics);
                    WriteEndExecuteEvent(success, sqlExceptionNumber, synchronous: true);
                }
            }
        }

        /// <include file='../../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlCommand.xml' path='docs/members[@name="SqlCommand"]/BeginExecuteXmlReader[@name="default"]/*'/>
        [System.Security.Permissions.HostProtectionAttribute(ExternalThreading = true)]
        public IAsyncResult BeginExecuteXmlReader()
        {
            // BeginExecuteXmlReader will track executiontime
            return BeginExecuteXmlReader(null, null);
        }

        /// <include file='../../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlCommand.xml' path='docs/members[@name="SqlCommand"]/BeginExecuteXmlReader[@name="AsyncCallbackAndstateObject"]/*'/>
        [System.Security.Permissions.HostProtectionAttribute(ExternalThreading = true)]
        public IAsyncResult BeginExecuteXmlReader(AsyncCallback callback, object stateObject)
        {
            SqlClientEventSource.Log.TryCorrelationTraceEvent("SqlCommand.BeginExecuteXmlReader | API | Correlation | Object Id {0}, Activity Id {1}, Client Connection Id {2}, Command Text '{3}'", ObjectID, ActivityCorrelator.Current, Connection?.ClientConnectionId, CommandText);
            SqlConnection.ExecutePermission.Demand();
            return BeginExecuteXmlReaderInternal(CommandBehavior.SequentialAccess, callback, stateObject, 0, isRetry: false);
        }

        private IAsyncResult BeginExecuteXmlReaderAsync(AsyncCallback callback, object stateObject)
        {
            SqlClientEventSource.Log.TryCorrelationTraceEvent("SqlCommand.BeginExecuteXmlReaderAsync | API | Correlation | Object Id {0}, Activity Id {1}, Client Connection Id {2}, Command Text '{3}'", ObjectID, ActivityCorrelator.Current, Connection?.ClientConnectionId, CommandText);
            return BeginExecuteXmlReaderInternal(CommandBehavior.SequentialAccess, callback, stateObject, CommandTimeout, isRetry: false, asyncWrite: true);
        }

        private IAsyncResult BeginExecuteXmlReaderInternal(CommandBehavior behavior, AsyncCallback callback, object stateObject, int timeout, bool isRetry, bool asyncWrite = false)
        {
            TaskCompletionSource<object> globalCompletion = new TaskCompletionSource<object>(stateObject);
            TaskCompletionSource<object> localCompletion = new TaskCompletionSource<object>(stateObject);

            if (!isRetry)
            {
                // Reset _pendingCancel upon entry into any Execute - used to synchronize state
                // between entry into Execute* API and the thread obtaining the stateObject.
                _pendingCancel = false;

                // Special case - done outside of try/catches to prevent putting a stateObj back into pool when we should not.
                ValidateAsyncCommand();
            }

            SqlStatistics statistics = null;
            try
            {
                if (!isRetry)
                {
                    statistics = SqlStatistics.StartTimer(Statistics);
                    WriteBeginExecuteEvent();
                }

                bool usedCache;
                Task writeTask;
                try
                {
                    // InternalExecuteNonQuery already has reliability block, but if failure will not put stateObj back into pool.
                    RunExecuteReader(
                        behavior,
                        RunBehavior.ReturnImmediately,
                        returnStream: true,
                        localCompletion,
                        timeout,
                        out writeTask,
                        out usedCache,
                        asyncWrite,
                        isRetry);
                }
                catch (Exception e)
                {
                    if (!ADP.IsCatchableOrSecurityExceptionType(e))
                    {
                        // If not catchable - the connection has already been caught and doomed in RunExecuteReader.
                        throw;
                    }

                    // For async, RunExecuteReader will never put the stateObj back into the pool, so do so now.
                    ReliablePutStateObject();
                    throw;
                }

                if (writeTask != null)
                {
                    AsyncHelper.ContinueTaskWithState(writeTask, localCompletion, this, (object state) => ((SqlCommand)state).BeginExecuteXmlReaderInternalReadStage(localCompletion));
                }
                else
                {
                    BeginExecuteXmlReaderInternalReadStage(localCompletion);
                }

                // When we use query caching for parameter encryption we need to retry on specific errors.
                // In these cases finalize the call internally and trigger a retry when needed.
                if (
                    !TriggerInternalEndAndRetryIfNecessary(
                        behavior,
                        stateObject,
                        timeout,
                        usedCache,
                        isRetry,
                        asyncWrite,
                        globalCompletion,
                        localCompletion,
                        endFunc: static (SqlCommand command, IAsyncResult asyncResult, bool isInternal, string endMethod) =>
                        {
                            return command.InternalEndExecuteReader(asyncResult, isInternal, endMethod);
                        },
                        retryFunc: static (SqlCommand command, CommandBehavior behavior, AsyncCallback callback, object stateObject, int timeout, bool isRetry, bool asyncWrite) =>
                        {
                            return command.BeginExecuteXmlReaderInternal(behavior, callback, stateObject, timeout, isRetry, asyncWrite);                    
                        },
                        endMethod: nameof(EndExecuteXmlReader)))
                {
                    globalCompletion = localCompletion;
                }

                // Add callback after work is done to avoid overlapping Begin/End methods
                if (callback != null)
                {
                    globalCompletion.Task.ContinueWith((t) => callback(t), TaskScheduler.Default);
                }
                return globalCompletion.Task;
            }
            finally
            {
                SqlStatistics.StopTimer(statistics);
            }
        }

        private void BeginExecuteXmlReaderInternalReadStage(TaskCompletionSource<object> completion)
        {
            Debug.Assert(completion != null, "Completion source should not be null");
            // Read SNI does not have catches for async exceptions, handle here.
            TdsParser bestEffortCleanupTarget = null;
            RuntimeHelpers.PrepareConstrainedRegions();
            try
            {
                bestEffortCleanupTarget = SqlInternalConnection.GetBestEffortCleanupTarget(_activeConnection);
                // must finish caching information before ReadSni which can activate the callback before returning
                CachedAsyncState.SetActiveConnectionAndResult(completion, nameof(EndExecuteXmlReader), _activeConnection);
                _stateObj.ReadSni(completion);
            }
            catch (System.OutOfMemoryException e)
            {
                _activeConnection.Abort(e);
                completion.TrySetException(e);
                throw;
            }
            catch (System.StackOverflowException e)
            {
                _activeConnection.Abort(e);
                completion.TrySetException(e);
                throw;
            }
            catch (System.Threading.ThreadAbortException e)
            {
                _activeConnection.Abort(e);
                SqlInternalConnection.BestEffortCleanup(bestEffortCleanupTarget);
                completion.TrySetException(e);
                throw;
            }
            catch (Exception e)
            {
                // Similarly, if an exception occurs put the stateObj back into the pool.
                // and reset async cache information to allow a second async execute
                CachedAsyncState?.ResetAsyncState();
                ReliablePutStateObject();
                completion.TrySetException(e);
            }
        }

        /// <include file='../../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlCommand.xml' path='docs/members[@name="SqlCommand"]/EndExecuteXmlReader[@name="IAsyncResult"]/*'/>
        public XmlReader EndExecuteXmlReader(IAsyncResult asyncResult)
        {
            try
            {
                return EndExecuteXmlReaderInternal(asyncResult);
            }
            finally
            {
                SqlClientEventSource.Log.TryCorrelationTraceEvent("SqlCommand.EndExecuteXmlReader | API | Correlation | Object Id {0}, Activity Id {1}, Client Connection Id {2}, Command Text '{3}'", ObjectID, ActivityCorrelator.Current, Connection?.ClientConnectionId, CommandText);
            }
        }

        private XmlReader EndExecuteXmlReaderAsync(IAsyncResult asyncResult)
        {
            SqlClientEventSource.Log.TryCorrelationTraceEvent("SqlCommand.EndExecuteXmlReaderAsync | API | Correlation | Object Id {0}, Activity Id {1}, Client Connection Id {2}, Command Text '{3}'", ObjectID, ActivityCorrelator.Current, Connection?.ClientConnectionId, CommandText);
            Debug.Assert(!_internalEndExecuteInitiated || _stateObj == null);

            Exception asyncException = ((Task)asyncResult).Exception;
            if (asyncException != null)
            {
                CachedAsyncState?.ResetAsyncState();
                ReliablePutStateObject();
                throw asyncException.InnerException;
            }
            else
            {
                ThrowIfReconnectionHasBeenCanceled();
                // lock on _stateObj prevents races with close/cancel.
                // If we have already initiate the End call internally, we have already done that, so no point doing it again.
                if (!_internalEndExecuteInitiated)
                {
                    lock (_stateObj)
                    {
                        return EndExecuteXmlReaderInternal(asyncResult);
                    }
                }
                else
                {
                    return EndExecuteXmlReaderInternal(asyncResult);
                }
            }
        }

        private XmlReader EndExecuteXmlReaderInternal(IAsyncResult asyncResult)
        {
            bool success = false;
            int? sqlExceptionNumber = null;
            try
            {
                XmlReader result = CompleteXmlReader(
                    InternalEndExecuteReader(asyncResult, isInternal: false,  nameof(EndExecuteXmlReader)),
                    isAsync: true);
                success = true;
                return result;
            }
            catch (SqlException e)
            {
                sqlExceptionNumber = e.Number;
                if (CachedAsyncState != null)
                {
                    CachedAsyncState.ResetAsyncState();
                };

                //  SqlException is always catchable 
                ReliablePutStateObject();
                throw;
            }
            catch (Exception e)
            {
                if (CachedAsyncState != null)
                {
                    CachedAsyncState.ResetAsyncState();
                };
                if (ADP.IsCatchableExceptionType(e))
                {
                    ReliablePutStateObject();
                };
                throw;
            }
            finally
            {
                WriteEndExecuteEvent(success, sqlExceptionNumber, synchronous: false);
            }
        }

        private XmlReader CompleteXmlReader(SqlDataReader ds, bool isAsync = false)
        {
            XmlReader xr = null;

            SmiExtendedMetaData[] md = ds.GetInternalSmiMetaData();
            bool isXmlCapable = (md != null && md.Length == 1 && (md[0].SqlDbType == SqlDbType.NText
                                                                  || md[0].SqlDbType == SqlDbType.NVarChar
                                                                  || md[0].SqlDbType == SqlDbType.Xml));

            if (isXmlCapable)
            {
                try
                {
                    SqlStream sqlBuf = new SqlStream(ds, true /*addByteOrderMark*/, (md[0].SqlDbType == SqlDbType.Xml) ? false : true /*process all rows*/);
                    xr = sqlBuf.ToXmlReader(isAsync);
                }
                catch (Exception e)
                {
                    if (ADP.IsCatchableExceptionType(e))
                    {
                        ds.Close();
                    }
                    throw;
                }
            }
            if (xr == null)
            {
                ds.Close();
                throw SQL.NonXmlResult();
            }
            return xr;
        }

        /// <include file='../../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlCommand.xml' path='docs/members[@name="SqlCommand"]/BeginExecuteXmlReader[@name="default"]/*'/>
        [HostProtection(ExternalThreading = true)]
        public IAsyncResult BeginExecuteReader() =>
            BeginExecuteReader(callback: null, stateObject: null, CommandBehavior.Default);

        /// <include file='../../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlCommand.xml' path='docs/members[@name="SqlCommand"]/BeginExecuteXmlReader[@name="AsyncCallbackAndstateObject"]/*'/>
        [HostProtection(ExternalThreading = true)]
        public IAsyncResult BeginExecuteReader(AsyncCallback callback, object stateObject) =>
            BeginExecuteReader(callback, stateObject, CommandBehavior.Default);

        /// <include file='../../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlCommand.xml' path='docs/members[@name="SqlCommand"]/BeginExecuteReader[@name="CommandBehavior"]/*'/>
        [HostProtection(ExternalThreading = true)]
        public IAsyncResult BeginExecuteReader(CommandBehavior behavior) =>
            BeginExecuteReader(callback: null, stateObject: null, behavior);

        /// <include file='../../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlCommand.xml' path='docs/members[@name="SqlCommand"]/BeginExecuteReader[@name="AsyncCallbackAndstateObjectAndCommandBehavior"]/*'/>
        [HostProtection(ExternalThreading = true)]
        public IAsyncResult BeginExecuteReader(AsyncCallback callback, object stateObject, CommandBehavior behavior)
        {
            SqlClientEventSource.Log.TryCorrelationTraceEvent("SqlCommand.BeginExecuteReader | API | Correlation | Object Id {0}, Behavior {1}, Activity Id {2}, Client Connection Id {3}, Command Text '{4}'", ObjectID, (int)behavior, ActivityCorrelator.Current, Connection?.ClientConnectionId, CommandText);
            SqlConnection.ExecutePermission.Demand();
            return BeginExecuteReaderInternal(behavior, callback, stateObject, 0, isRetry: false);
        }

        /// <include file='../../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlCommand.xml' path='docs/members[@name="SqlCommand"]/ExecuteDbDataReader[@name="CommandBehavior"]/*'/>
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            SqlClientEventSource.Log.TryCorrelationTraceEvent("SqlCommand.ExecuteDbDataReader | API | Correlation | Object Id {0}, Activity Id {1}, Client Connection Id {2}, Command Text '{3}'", ObjectID, ActivityCorrelator.Current, Connection?.ClientConnectionId, CommandText);
            return ExecuteReader(behavior);
        }

        /// <include file='../../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlCommand.xml' path='docs/members[@name="SqlCommand"]/ExecuteReader[@name="default"]/*'/>
        new public SqlDataReader ExecuteReader()
        {
            SqlStatistics statistics = null;
            SqlClientEventSource.Log.TryCorrelationTraceEvent("SqlCommand.ExecuteReader | API | Correlation | ObjectID {0}, Activity Id {1}, Client Connection Id {2}, Command Text '{3}'", ObjectID, ActivityCorrelator.Current, Connection?.ClientConnectionId, CommandText);
            try
            {
                statistics = SqlStatistics.StartTimer(Statistics);
                return ExecuteReader(CommandBehavior.Default);
            }
            finally
            {
                SqlStatistics.StopTimer(statistics);
            }
        }

        /// <include file='../../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlCommand.xml' path='docs/members[@name="SqlCommand"]/ExecuteReader[@name="CommandBehavior"]/*'/>
        new public SqlDataReader ExecuteReader(CommandBehavior behavior)
        {
            SqlConnection.ExecutePermission.Demand();

            // Reset _pendingCancel upon entry into any Execute - used to synchronize state
            // between entry into Execute* API and the thread obtaining the stateObject.
            _pendingCancel = false;

            SqlStatistics statistics = null;
            TdsParser bestEffortCleanupTarget = null;
            RuntimeHelpers.PrepareConstrainedRegions();
            bool success = false;
            int? sqlExceptionNumber = null;

            using (TryEventScope.Create("SqlCommand.ExecuteReader | API | Object Id {0}", ObjectID))
            {
                try
                {
                    WriteBeginExecuteEvent();
                    bestEffortCleanupTarget = SqlInternalConnection.GetBestEffortCleanupTarget(_activeConnection);
                    statistics = SqlStatistics.StartTimer(Statistics);
                    SqlDataReader result = IsProviderRetriable ?
                        RunExecuteReaderWithRetry(behavior, RunBehavior.ReturnImmediately, returnStream: true) :
                        RunExecuteReader(behavior, RunBehavior.ReturnImmediately, true);
                    success = true;
                    return result;
                }
                catch (SqlException e)
                {
                    sqlExceptionNumber = e.Number;
                    throw;
                }
                catch (System.OutOfMemoryException e)
                {
                    _activeConnection.Abort(e);
                    throw;
                }
                catch (System.StackOverflowException e)
                {
                    _activeConnection.Abort(e);
                    throw;
                }
                catch (System.Threading.ThreadAbortException e)
                {
                    _activeConnection.Abort(e);
                    SqlInternalConnection.BestEffortCleanup(bestEffortCleanupTarget);
                    throw;
                }
                finally
                {
                    SqlStatistics.StopTimer(statistics);
                    WriteEndExecuteEvent(success, sqlExceptionNumber, synchronous: true);
                }
            }
        }

        /// <include file='../../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlCommand.xml' path='docs/members[@name="SqlCommand"]/EndExecuteReader[@name="IAsyncResult2"]/*'/>
        public SqlDataReader EndExecuteReader(IAsyncResult asyncResult)
        {
            try
            {
                return EndExecuteReaderInternal(asyncResult);
            }
            finally
            {
                SqlClientEventSource.Log.TryCorrelationTraceEvent("SqlCommand.EndExecuteReader | API | Correlation | Object Id {0}, Activity Id {1}, Client Connection Id {2}, Command Text '{3}'", ObjectID, ActivityCorrelator.Current, Connection?.ClientConnectionId, CommandText);
            }
        }

        private SqlDataReader EndExecuteReaderAsync(IAsyncResult asyncResult)
        {
            SqlClientEventSource.Log.TryCorrelationTraceEvent("SqlCommand.EndExecuteReaderAsync | API | Correlation | Object Id {0}, Activity Id {1}, Client Connection Id {2}, Command Text '{3}'", ObjectID, ActivityCorrelator.Current, Connection?.ClientConnectionId, CommandText);
            Debug.Assert(!_internalEndExecuteInitiated || _stateObj == null);

            Exception asyncException = ((Task)asyncResult).Exception;
            if (asyncException != null)
            {
                CachedAsyncState?.ResetAsyncState();
                ReliablePutStateObject();
                throw asyncException.InnerException;
            }
            else
            {
                ThrowIfReconnectionHasBeenCanceled();
                // lock on _stateObj prevents races with close/cancel.
                if (!_internalEndExecuteInitiated)
                {
                    lock (_stateObj)
                    {
                        return EndExecuteReaderInternal(asyncResult);
                    }
                }
                else
                {
                    return EndExecuteReaderInternal(asyncResult);
                }
            }
        }

        private SqlDataReader EndExecuteReaderInternal(IAsyncResult asyncResult)
        {
            SqlClientEventSource.Log.TryTraceEvent("SqlCommand.EndExecuteReaderInternal | API | ObjectId {0}, Client Connection Id {1}, MARS={2}, AsyncCommandInProgress={3}",
                                                    _activeConnection?.ObjectID, _activeConnection?.ClientConnectionId,
                                                    _activeConnection?.Parser?.MARSOn, _activeConnection?.AsyncCommandInProgress);
            SqlStatistics statistics = null;
            bool success = false;
            int? sqlExceptionNumber = null;
            try
            {
                statistics = SqlStatistics.StartTimer(Statistics);
                SqlDataReader result = InternalEndExecuteReader(
                    asyncResult,
                    isInternal: false,
                    nameof(EndExecuteReader));
                success = true;
                return result;
            }
            catch (SqlException e)
            {
                sqlExceptionNumber = e.Number;
                if (CachedAsyncState != null)
                {
                    CachedAsyncState.ResetAsyncState();
                };

                //  SqlException is always catchable 
                ReliablePutStateObject();
                throw;
            }
            catch (Exception e)
            {
                if (CachedAsyncState != null)
                {
                    CachedAsyncState.ResetAsyncState();
                };
                if (ADP.IsCatchableExceptionType(e))
                {
                    ReliablePutStateObject();
                };
                throw;
            }
            finally
            {
                SqlStatistics.StopTimer(statistics);
                WriteEndExecuteEvent(success, sqlExceptionNumber, synchronous: false);
            }
        }

        private void CleanupExecuteReaderAsync(Task<SqlDataReader> task, TaskCompletionSource<SqlDataReader> source, Guid operationId)
        {
            if (task.IsFaulted)
            {
                Exception e = task.Exception.InnerException;
                source.SetException(e);
            }
            else
            {
                if (task.IsCanceled)
                {
                    source.SetCanceled();
                }
                else
                {
                    source.SetResult(task.Result);
                }
            }
        }

        private IAsyncResult BeginExecuteReaderAsync(CommandBehavior behavior, AsyncCallback callback, object stateObject)
        {
            return BeginExecuteReaderInternal(behavior, callback, stateObject, CommandTimeout, isRetry: false, asyncWrite: true);
        }

        private IAsyncResult BeginExecuteReaderInternal(CommandBehavior behavior, AsyncCallback callback, object stateObject, int timeout, bool isRetry, bool asyncWrite = false)
        {
            TaskCompletionSource<object> globalCompletion = new TaskCompletionSource<object>(stateObject);
            TaskCompletionSource<object> localCompletion = new TaskCompletionSource<object>(stateObject);

            if (!isRetry)
            {
                // Reset _pendingCancel upon entry into any Execute - used to synchronize state
                // between entry into Execute* API and the thread obtaining the stateObject.
                _pendingCancel = false;
            }

            SqlStatistics statistics = null;
            try
            {
                if (!isRetry)
                {
                    statistics = SqlStatistics.StartTimer(Statistics);
                    WriteBeginExecuteEvent();

                    ValidateAsyncCommand(); // Special case - done outside of try/catches to prevent putting a stateObj
                                            // back into pool when we should not.
                }

                bool usedCache = false;
                Task writeTask = null;
                try
                {
                    // InternalExecuteNonQuery already has reliability block, but if failure will not put stateObj back into pool.
                    RunExecuteReader(
                        behavior,
                        RunBehavior.ReturnImmediately,
                        returnStream: true,
                        localCompletion,
                        timeout,
                        out writeTask,
                        out usedCache,
                        asyncWrite,
                        isRetry,
                        nameof(BeginExecuteReader));
                }
                catch (Exception e)
                {
                    if (!ADP.IsCatchableOrSecurityExceptionType(e))
                    {
                        // If not catchable - the connection has already been caught and doomed in RunExecuteReader.
                        throw;
                    }

                    // For async, RunExecuteReader will never put the stateObj back into the pool, so do so now.
                    ReliablePutStateObject();
                    throw;
                }

                if (writeTask != null)
                {
                    AsyncHelper.ContinueTaskWithState(writeTask, localCompletion, this, (object state) => ((SqlCommand)state).BeginExecuteReaderInternalReadStage(localCompletion));
                }
                else
                {
                    BeginExecuteReaderInternalReadStage(localCompletion);
                }

                // When we use query caching for parameter encryption we need to retry on specific errors.
                // In these cases finalize the call internally and trigger a retry when needed.
                if (
                    !TriggerInternalEndAndRetryIfNecessary(
                        behavior,
                        stateObject,
                        timeout,
                        usedCache,
                        isRetry,
                        asyncWrite,
                        globalCompletion,
                        localCompletion,
                        endFunc: static (SqlCommand command, IAsyncResult asyncResult, bool isInternal, string endMethod) =>
                        {
                            return command.InternalEndExecuteReader(asyncResult, isInternal, endMethod);
                        },
                        retryFunc: static (SqlCommand command, CommandBehavior behavior, AsyncCallback callback, object stateObject, int timeout, bool isRetry, bool asyncWrite) =>
                        {
                            return command.BeginExecuteReaderInternal(behavior, callback, stateObject, timeout, isRetry, asyncWrite);
                        },
                        nameof(EndExecuteReader)))
                {
                    globalCompletion = localCompletion;
                }

                // Add callback after work is done to avoid overlapping Begin/End methods
                if (callback != null)
                {
                    globalCompletion.Task.ContinueWith((t) => callback(t), TaskScheduler.Default);
                }

                return globalCompletion.Task;
            }
            finally
            {
                SqlStatistics.StopTimer(statistics);
            }
        }

        private bool TriggerInternalEndAndRetryIfNecessary(
            CommandBehavior behavior,
            object stateObject,
            int timeout,
            bool usedCache,
            bool isRetry,
            bool asyncWrite,
            TaskCompletionSource<object> globalCompletion,
            TaskCompletionSource<object> localCompletion,
            Func<SqlCommand, IAsyncResult, bool, string, object> endFunc,
            Func<SqlCommand, CommandBehavior, AsyncCallback, object, int, bool, bool, IAsyncResult> retryFunc,
            string endMethod)
        {
            // We shouldn't be using the cache if we are in retry.
            Debug.Assert(!usedCache || !isRetry);

            // If column encryption is enabled and we used the cache, we want to catch any potential exceptions that were caused by the query cache and retry if the error indicates that we should.
            // So, try to read the result of the query before completing the overall task and trigger a retry if appropriate.
            if ((IsColumnEncryptionEnabled && !isRetry && (usedCache || ShouldUseEnclaveBasedWorkflow))
#if DEBUG
                || _forceInternalEndQuery
#endif
                )
            {
                long firstAttemptStart = ADP.TimerCurrent();

                localCompletion.Task.ContinueWith(tsk =>
                {
                    if (tsk.IsFaulted)
                    {
                        globalCompletion.TrySetException(tsk.Exception.InnerException);
                    }
                    else if (tsk.IsCanceled)
                    {
                        globalCompletion.TrySetCanceled();
                    }
                    else
                    {
                        try
                        {
                            // Mark that we initiated the internal EndExecute. This should always be false until we set it here.
                            Debug.Assert(!_internalEndExecuteInitiated);
                            _internalEndExecuteInitiated = true;

                            // lock on _stateObj prevents races with close/cancel.
                            lock (_stateObj)
                            {
                                endFunc(this, tsk, /*isInternal:*/ true, endMethod);
                            }
                            globalCompletion.TrySetResult(tsk.Result);
                        }
                        catch (Exception e)
                        {
                            // Put the state object back to the cache.
                            // Do not reset the async state, since this is managed by the user Begin/End and not internally.
                            if (ADP.IsCatchableExceptionType(e))
                            {
                                ReliablePutStateObject();
                            }

                            bool shouldRetry = e is EnclaveDelegate.RetryableEnclaveQueryExecutionException;

                            // Check if we have an error indicating that we can retry.
                            if (e is SqlException)
                            {
                                SqlException sqlEx = e as SqlException;

                                for (int i = 0; i < sqlEx.Errors.Count; i++)
                                {
                                    if ((usedCache && (sqlEx.Errors[i].Number == TdsEnums.TCE_CONVERSION_ERROR_CLIENT_RETRY)) ||
                                         (ShouldUseEnclaveBasedWorkflow && (sqlEx.Errors[i].Number == TdsEnums.TCE_ENCLAVE_INVALID_SESSION_HANDLE)))
                                    {
                                        shouldRetry = true;
                                        break;
                                    }
                                }
                            }

                            if (!shouldRetry)
                            {
                                // If we cannot retry, Reset the async state to make sure we leave a clean state.
                                if (CachedAsyncState != null)
                                {
                                    CachedAsyncState.ResetAsyncState();
                                }
                                try
                                {
                                    _activeConnection.GetOpenTdsConnection().DecrementAsyncCount();

                                    globalCompletion.TrySetException(e);
                                }
                                catch (Exception e2)
                                {
                                    globalCompletion.TrySetException(e2);
                                }
                            }
                            else
                            {
                                // Remove the entry from the cache since it was inconsistent.
                                SqlQueryMetadataCache.GetInstance().InvalidateCacheEntry(this);

                                InvalidateEnclaveSession();

                                try
                                {
                                    // Kick off the retry.
                                    _internalEndExecuteInitiated = false;
                                    Task<object> retryTask = (Task<object>)retryFunc(
                                        this,
                                        behavior,
                                        null,
                                        stateObject,
                                        TdsParserStaticMethods.GetRemainingTimeout(timeout, firstAttemptStart),
                                        /*isRetry:*/ true,
                                        asyncWrite);

                                    retryTask.ContinueWith(
                                        static (Task<object> retryTask, object state) =>
                                        {
                                            TaskCompletionSource<object> completion = (TaskCompletionSource<object>)state;
                                            if (retryTask.IsFaulted)
                                            {
                                                completion.TrySetException(retryTask.Exception.InnerException);
                                            }
                                            else if (retryTask.IsCanceled)
                                            {
                                                completion.TrySetCanceled();
                                            }
                                            else
                                            {
                                                completion.TrySetResult(retryTask.Result);
                                            }
                                        },
                                        state: globalCompletion,
                                        TaskScheduler.Default
                                    );
                                }
                                catch (Exception e2)
                                {
                                    globalCompletion.TrySetException(e2);
                                }
                            }
                        }
                    }
                }, TaskScheduler.Default);

                return true;
            }
            else
            {
                return false;
            }
        }

        private void InvalidateEnclaveSession()
        {
            if (ShouldUseEnclaveBasedWorkflow && this.enclavePackage != null)
            {
                EnclaveDelegate.Instance.InvalidateEnclaveSession(
                    this._activeConnection.AttestationProtocol,
                    this._activeConnection.Parser.EnclaveType,
                    GetEnclaveSessionParameters(),
                    this.enclavePackage.EnclaveSession);
            }
        }

        private EnclaveSessionParameters GetEnclaveSessionParameters()
        {
            return new EnclaveSessionParameters(
                this._activeConnection.DataSource,
                this._activeConnection.EnclaveAttestationUrl,
                this._activeConnection.Database);
        }

        private void BeginExecuteReaderInternalReadStage(TaskCompletionSource<object> completion)
        {
            Debug.Assert(completion != null, "CompletionSource should not be null");
            SqlClientEventSource.Log.TryCorrelationTraceEvent("SqlCommand.BeginExecuteReaderInternalReadStage | INFO | Correlation | Object Id {0}, Activity Id {1}, Client Connection Id {2}, Command Text '{3}'", ObjectID, ActivityCorrelator.Current, Connection?.ClientConnectionId, CommandText);
            // Read SNI does not have catches for async exceptions, handle here.
            TdsParser bestEffortCleanupTarget = null;
            RuntimeHelpers.PrepareConstrainedRegions();
            try
            {
                bestEffortCleanupTarget = SqlInternalConnection.GetBestEffortCleanupTarget(_activeConnection);
                // must finish caching information before ReadSni which can activate the callback before returning
                CachedAsyncState.SetActiveConnectionAndResult(completion, nameof(EndExecuteReader), _activeConnection);
                _stateObj.ReadSni(completion);
            }
            catch (System.OutOfMemoryException e)
            {
                _activeConnection.Abort(e);
                completion.TrySetException(e);
                throw;
            }
            catch (System.StackOverflowException e)
            {
                _activeConnection.Abort(e);
                completion.TrySetException(e);
                throw;
            }
            catch (System.Threading.ThreadAbortException e)
            {
                _activeConnection.Abort(e);
                SqlInternalConnection.BestEffortCleanup(bestEffortCleanupTarget);
                completion.TrySetException(e);
                throw;
            }
            catch (Exception e)
            {
                // Similarly, if an exception occurs put the stateObj back into the pool.
                // and reset async cache information to allow a second async execute
                CachedAsyncState?.ResetAsyncState();
                ReliablePutStateObject();
                completion.TrySetException(e);
            }
        }

        private SqlDataReader InternalEndExecuteReader(IAsyncResult asyncResult, bool isInternal, string endMethod)
        {
            SqlClientEventSource.Log.TryTraceEvent("SqlCommand.InternalEndExecuteReader | INFO | ObjectId {0}, Client Connection Id {1}, MARS={2}, AsyncCommandInProgress={3}",
                                                    _activeConnection?.ObjectID, _activeConnection?.ClientConnectionId,
                                                    _activeConnection?.Parser?.MARSOn, _activeConnection?.AsyncCommandInProgress);
            VerifyEndExecuteState((Task)asyncResult, endMethod);
            WaitForAsyncResults(asyncResult, isInternal);

            // If column encryption is enabled, also check the state after waiting for the task.
            // It would be better to do this for all cases, but avoiding for compatibility reasons.
            if (IsColumnEncryptionEnabled)
            {
                VerifyEndExecuteState((Task)asyncResult, endMethod, fullCheckForColumnEncryption: true);
            }

            CheckThrowSNIException();

            TdsParser bestEffortCleanupTarget = null;
            RuntimeHelpers.PrepareConstrainedRegions();
            try
            {
                bestEffortCleanupTarget = SqlInternalConnection.GetBestEffortCleanupTarget(_activeConnection);
                SqlDataReader reader = CompleteAsyncExecuteReader(isInternal);
                Debug.Assert(_stateObj == null, "non-null state object in InternalEndExecuteReader");
                return reader;
            }
            catch (System.OutOfMemoryException e)
            {
                _activeConnection.Abort(e);
                throw;
            }
            catch (System.StackOverflowException e)
            {
                _activeConnection.Abort(e);
                throw;
            }
            catch (System.Threading.ThreadAbortException e)
            {
                _activeConnection.Abort(e);
                SqlInternalConnection.BestEffortCleanup(bestEffortCleanupTarget);
                throw;
            }
        }

        /// <include file='../../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlCommand.xml' path='docs/members[@name="SqlCommand"]/ExecuteNonQueryAsync[@name="CancellationToken"]/*'/>
        public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken) =>
            IsProviderRetriable
                ? InternalExecuteNonQueryWithRetryAsync(cancellationToken)
                : InternalExecuteNonQueryAsync(cancellationToken);

        private Task<int> InternalExecuteNonQueryWithRetryAsync(CancellationToken cancellationToken) =>
            RetryLogicProvider.ExecuteAsync(
                sender: this,
                () => InternalExecuteNonQueryAsync(cancellationToken),
                cancellationToken);

        private Task<int> InternalExecuteNonQueryAsync(CancellationToken cancellationToken)
        {
            SqlClientEventSource.Log.TryCorrelationTraceEvent("SqlCommand.InternalExecuteNonQueryAsync | API | Correlation | Object Id {0}, Activity Id {1}, Client Connection Id {2}, Command Text '{3}'", ObjectID, ActivityCorrelator.Current, Connection?.ClientConnectionId, CommandText);
            SqlConnection.ExecutePermission.Demand();
            Guid operationId = Guid.Empty;

            // connection can be used as state in RegisterForConnectionCloseNotification continuation
            // to avoid an allocation so use it as the state value if possible but it can be changed if
            // you need it for a more important piece of data that justifies the tuple allocation later
            TaskCompletionSource<int> source = new TaskCompletionSource<int>(_activeConnection);

            CancellationTokenRegistration registration = new CancellationTokenRegistration();
            if (cancellationToken.CanBeCanceled)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    source.SetCanceled();
                    return source.Task;
                }
                registration = cancellationToken.Register(s_cancelIgnoreFailure, this);
            }

            Task<int> returnedTask = source.Task;

            ExecuteNonQueryAsyncCallContext context = new ExecuteNonQueryAsyncCallContext();
            context.Set(this, source, registration, operationId);
            try
            {
                returnedTask = RegisterForConnectionCloseNotification(returnedTask);

                Task<int>.Factory.FromAsync(
                    beginMethod: static (AsyncCallback callback, object stateObject) =>
                    {
                        return ((ExecuteNonQueryAsyncCallContext)stateObject).Command.BeginExecuteNonQueryAsync(callback, stateObject);
                    },
                    endMethod: static (IAsyncResult asyncResult) =>
                    {
                        return ((ExecuteNonQueryAsyncCallContext)asyncResult.AsyncState).Command.EndExecuteNonQueryAsync(asyncResult);
                    },
                    state: context
                )
                .ContinueWith(
                    static (Task<int> task) =>
                    {
                        ExecuteNonQueryAsyncCallContext context = (ExecuteNonQueryAsyncCallContext)task.AsyncState;
                        SqlCommand command = context.Command;
                        Guid operationId = context.OperationID;
                        TaskCompletionSource<int> source = context.TaskCompletionSource;
                        context.Dispose();

                        command.CleanupAfterExecuteNonQueryAsync(task, source, operationId);
                    },
                    scheduler: TaskScheduler.Default
                );
            }
            catch (Exception e)
            {
                source.SetException(e);
                context.Dispose();
            }

            return returnedTask;
        }

        private void CleanupAfterExecuteNonQueryAsync(Task<int> task, TaskCompletionSource<int> source, Guid operationId)
        {
            if (task.IsFaulted)
            {
                Exception e = task.Exception.InnerException;
                source.SetException(e);
            }
            else
            {
                if (task.IsCanceled)
                {
                    source.SetCanceled();
                }
                else
                {
                    source.SetResult(task.Result);
                }
            }
        }

        /// <include file='../../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlCommand.xml' path='docs/members[@name="SqlCommand"]/ExecuteDbDataReaderAsync/*'/>
        protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
        {
            return ExecuteReaderAsync(behavior, cancellationToken).ContinueWith<DbDataReader>(
                static (Task<SqlDataReader> result) =>
                {
                    if (result.IsFaulted)
                    {
                        throw result.Exception.InnerException;
                    }
                    return result.Result;
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.NotOnCanceled,
                TaskScheduler.Default
            );
        }

        /// <include file='../../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlCommand.xml' path='docs/members[@name="SqlCommand"]/ExecuteReaderAsync[@name="default"]/*'/>
        public new Task<SqlDataReader> ExecuteReaderAsync() =>
            ExecuteReaderAsync(CommandBehavior.Default, CancellationToken.None);

        /// <include file='../../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlCommand.xml' path='docs/members[@name="SqlCommand"]/ExecuteReaderAsync[@name="CommandBehavior"]/*'/>
        public new Task<SqlDataReader> ExecuteReaderAsync(CommandBehavior behavior) =>
            ExecuteReaderAsync(behavior, CancellationToken.None);

        /// <include file='../../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlCommand.xml' path='docs/members[@name="SqlCommand"]/ExecuteReaderAsync[@name="CancellationToken"]/*'/>
        public new Task<SqlDataReader> ExecuteReaderAsync(CancellationToken cancellationToken) =>
            ExecuteReaderAsync(CommandBehavior.Default, cancellationToken);

        /// <include file='../../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlCommand.xml' path='docs/members[@name="SqlCommand"]/ExecuteReaderAsync[@name="commandBehaviorAndCancellationToken"]/*'/>
        public new Task<SqlDataReader> ExecuteReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken) =>
            IsProviderRetriable
                ? InternalExecuteReaderWithRetryAsync(behavior, cancellationToken)
                : InternalExecuteReaderAsync(behavior, cancellationToken);

        private Task<SqlDataReader> InternalExecuteReaderWithRetryAsync(CommandBehavior behavior, CancellationToken cancellationToken) =>
            RetryLogicProvider.ExecuteAsync(
                sender: this,
                () => InternalExecuteReaderAsync(behavior, cancellationToken),
                cancellationToken);

        private Task<SqlDataReader> InternalExecuteReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
        {
            SqlClientEventSource.Log.TryCorrelationTraceEvent("SqlCommand.InternalExecuteReaderAsync | API | Correlation | Object Id {0}, Behavior {1}, Activity Id {2}, Client Connection Id {3}, Command Text '{4}'", ObjectID, (int)behavior, ActivityCorrelator.Current, Connection?.ClientConnectionId, CommandText);
            SqlClientEventSource.Log.TryTraceEvent("SqlCommand.InternalExecuteReaderAsync | API> {0}, Client Connection Id {1}, Command Text = '{2}'", ObjectID, Connection?.ClientConnectionId, CommandText);
            SqlConnection.ExecutePermission.Demand();
            Guid operationId = default(Guid);

            // connection can be used as state in RegisterForConnectionCloseNotification continuation
            // to avoid an allocation so use it as the state value if possible but it can be changed if
            // you need it for a more important piece of data that justifies the tuple allocation later
            TaskCompletionSource<SqlDataReader> source = new TaskCompletionSource<SqlDataReader>(_activeConnection);

            CancellationTokenRegistration registration = new CancellationTokenRegistration();
            if (cancellationToken.CanBeCanceled)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    source.SetCanceled();
                    return source.Task;
                }
                registration = cancellationToken.Register(s_cancelIgnoreFailure, this);
            }

            Task<SqlDataReader> returnedTask = source.Task;
            ExecuteReaderAsyncCallContext context = null;
            try
            {
                returnedTask = RegisterForConnectionCloseNotification(returnedTask);

                if (_activeConnection?.InnerConnection is SqlInternalConnection sqlInternalConnection)
                {
                    context = Interlocked.Exchange(ref sqlInternalConnection.CachedCommandExecuteReaderAsyncContext, null);
                }
                if (context is null)
                {
                    context = new ExecuteReaderAsyncCallContext();
                }
                context.Set(this, source, registration, behavior, operationId);

                Task<SqlDataReader>.Factory.FromAsync(
                    beginMethod: static (AsyncCallback callback, object stateObject) =>
                    {
                        ExecuteReaderAsyncCallContext args = (ExecuteReaderAsyncCallContext)stateObject;
                        return args.Command.BeginExecuteReaderInternal(args.CommandBehavior, callback, stateObject, args.Command.CommandTimeout, isRetry: false, asyncWrite: true);
                    },
                    endMethod: static (IAsyncResult asyncResult) =>
                    {
                        ExecuteReaderAsyncCallContext args = (ExecuteReaderAsyncCallContext)asyncResult.AsyncState;
                        return args.Command.EndExecuteReaderAsync(asyncResult);
                    },
                    state: context
                ).ContinueWith(
                    continuationAction: static (Task<SqlDataReader> task) =>
                    {
                        ExecuteReaderAsyncCallContext context = (ExecuteReaderAsyncCallContext)task.AsyncState;
                        SqlCommand command = context.Command;
                        Guid operationId = context.OperationID;
                        TaskCompletionSource<SqlDataReader> source = context.TaskCompletionSource;
                        context.Dispose();

                        command.CleanupExecuteReaderAsync(task, source, operationId);
                    },
                    scheduler: TaskScheduler.Default
                );
            }
            catch (Exception e)
            {
                source.SetException(e);
                context?.Dispose();
            }

            return returnedTask;
        }

        private void SetCachedCommandExecuteReaderAsyncContext(ExecuteReaderAsyncCallContext instance)
        {
            if (_activeConnection?.InnerConnection is SqlInternalConnection sqlInternalConnection)
            {
                Interlocked.CompareExchange(ref sqlInternalConnection.CachedCommandExecuteReaderAsyncContext, instance, null);
            }
        }

        private void SetCachedCommandExecuteNonQueryAsyncContext(ExecuteNonQueryAsyncCallContext instance)
        {
            if (_activeConnection?.InnerConnection is SqlInternalConnection sqlInternalConnection)
            {
                Interlocked.CompareExchange(ref sqlInternalConnection.CachedCommandExecuteNonQueryAsyncContext, instance, null);
            }
        }

        private void SetCachedCommandExecuteXmlReaderContext(ExecuteXmlReaderAsyncCallContext instance)
        {
            if (_activeConnection?.InnerConnection is SqlInternalConnection sqlInternalConnection)
            {
                Interlocked.CompareExchange(ref sqlInternalConnection.CachedCommandExecuteXmlReaderAsyncContext, instance, null);
            }
        }

        /// <include file='../../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlCommand.xml' path='docs/members[@name="SqlCommand"]/ExecuteScalarAsync[@name="CancellationToken"]/*'/>
        public override Task<object> ExecuteScalarAsync(CancellationToken cancellationToken) =>
            // Do not use retry logic here as internal call to ExecuteReaderAsync handles retry logic.
            InternalExecuteScalarAsync(cancellationToken);

        private Task<object> InternalExecuteScalarAsync(CancellationToken cancellationToken)
        {
            return ExecuteReaderAsync(cancellationToken).ContinueWith((executeTask) =>
            {
                TaskCompletionSource<object> source = new TaskCompletionSource<object>();
                if (executeTask.IsCanceled)
                {
                    source.SetCanceled();
                }
                else if (executeTask.IsFaulted)
                {
                    source.SetException(executeTask.Exception.InnerException);
                }
                else
                {
                    SqlDataReader reader = executeTask.Result;
                    reader.ReadAsync(cancellationToken)
                        .ContinueWith((Task<bool> readTask) =>
                        {
                            try
                            {
                                if (readTask.IsCanceled)
                                {
                                    reader.Dispose();
                                    source.SetCanceled();
                                }
                                else if (readTask.IsFaulted)
                                {
                                    reader.Dispose();
                                    source.SetException(readTask.Exception.InnerException);
                                }
                                else
                                {
                                    Exception exception = null;
                                    object result = null;
                                    try
                                    {
                                        bool more = readTask.Result;
                                        if (more && reader.FieldCount > 0)
                                        {
                                            try
                                            {
                                                result = reader.GetValue(0);
                                            }
                                            catch (Exception e)
                                            {
                                                exception = e;
                                            }
                                        }
                                    }
                                    finally
                                    {
                                        reader.Dispose();
                                    }
                                    if (exception != null)
                                    {
                                        source.SetException(exception);
                                    }
                                    else
                                    {
                                        source.SetResult(result);
                                    }
                                }
                            }
                            catch (Exception e)
                            {
                                // exception thrown by Dispose...
                                source.SetException(e);
                            }
                        },
                        TaskScheduler.Default
                    );
                }
                return source.Task;
            }, TaskScheduler.Default).Unwrap();
        }

        /// <include file='../../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlCommand.xml' path='docs/members[@name="SqlCommand"]/ExecuteXmlReaderAsync[@name="default"]/*'/>
        public Task<XmlReader> ExecuteXmlReaderAsync() => 
            ExecuteXmlReaderAsync(CancellationToken.None);

        /// <include file='../../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlCommand.xml' path='docs/members[@name="SqlCommand"]/ExecuteXmlReaderAsync[@name="CancellationToken"]/*'/>
        public Task<XmlReader> ExecuteXmlReaderAsync(CancellationToken cancellationToken) =>
            IsProviderRetriable
                ? InternalExecuteXmlReaderWithRetryAsync(cancellationToken)
                : InternalExecuteXmlReaderAsync(cancellationToken);

        private Task<XmlReader> InternalExecuteXmlReaderWithRetryAsync(CancellationToken cancellationToken) =>
            RetryLogicProvider.ExecuteAsync(
                sender: this,
                () => InternalExecuteXmlReaderAsync(cancellationToken),
                cancellationToken);

        private Task<XmlReader> InternalExecuteXmlReaderAsync(CancellationToken cancellationToken)
        {
            SqlClientEventSource.Log.TryCorrelationTraceEvent("SqlCommand.InternalExecuteXmlReaderAsync | API | Correlation | Object Id {0}, Activity Id {1}, Client Connection Id {2}, Command Text '{3}'", ObjectID, ActivityCorrelator.Current, Connection?.ClientConnectionId, CommandText);
            SqlConnection.ExecutePermission.Demand();
            Guid operationId = Guid.Empty;

            // connection can be used as state in RegisterForConnectionCloseNotification continuation
            // to avoid an allocation so use it as the state value if possible but it can be changed if
            // you need it for a more important piece of data that justifies the tuple allocation later
            TaskCompletionSource<XmlReader> source = new TaskCompletionSource<XmlReader>(_activeConnection);

            CancellationTokenRegistration registration = new CancellationTokenRegistration();
            if (cancellationToken.CanBeCanceled)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    source.SetCanceled();
                    return source.Task;
                }
                registration = cancellationToken.Register(s_cancelIgnoreFailure, this);
            }

            ExecuteXmlReaderAsyncCallContext context = null;
            if (_activeConnection?.InnerConnection is SqlInternalConnection sqlInternalConnection)
            {
                context = Interlocked.Exchange(ref sqlInternalConnection.CachedCommandExecuteXmlReaderAsyncContext, null);
            }
            if (context is null)
            {
                context = new ExecuteXmlReaderAsyncCallContext();
            }
            context.Set(this, source, registration, operationId);

            Task<XmlReader> returnedTask = source.Task;
            try
            {
                returnedTask = RegisterForConnectionCloseNotification(returnedTask);

                Task<XmlReader>.Factory.FromAsync(
                    beginMethod: static (AsyncCallback callback, object stateObject) =>
                    {
                        return ((ExecuteXmlReaderAsyncCallContext)stateObject).Command.BeginExecuteXmlReaderAsync(callback, stateObject);
                    },
                    endMethod: static (IAsyncResult asyncResult) =>
                    {
                        return ((ExecuteXmlReaderAsyncCallContext)asyncResult.AsyncState).Command.EndExecuteXmlReaderAsync(asyncResult);
                    },
                    state: context
                ).ContinueWith(
                    static (Task<XmlReader> task) =>
                    {
                        ExecuteXmlReaderAsyncCallContext context = (ExecuteXmlReaderAsyncCallContext)task.AsyncState;
                        SqlCommand command = context.Command;
                        Guid operationId = context.OperationID;
                        TaskCompletionSource<XmlReader> source = context.TaskCompletionSource;
                        context.Dispose();

                        command.CleanupAfterExecuteXmlReaderAsync(task, source, operationId);
                    },
                    TaskScheduler.Default
                );
            }
            catch (Exception e)
            {
                source.SetException(e);
            }

            return returnedTask;
        }

        private void CleanupAfterExecuteXmlReaderAsync(Task<XmlReader> task, TaskCompletionSource<XmlReader> source, Guid operationId)
        {
            if (task.IsFaulted)
            {
                Exception e = task.Exception.InnerException;
                source.SetException(e);
            }
            else
            {
                if (task.IsCanceled)
                {
                    source.SetCanceled();
                }
                else
                {
                    source.SetResult(task.Result);
                }
            }
        }

        /// <include file='../../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlCommand.xml' path='docs/members[@name="SqlCommand"]/RegisterColumnEncryptionKeyStoreProvidersOnCommand/*' />
        public void RegisterColumnEncryptionKeyStoreProvidersOnCommand(IDictionary<string, SqlColumnEncryptionKeyStoreProvider> customProviders)
        {
            ValidateCustomProviders(customProviders);

            // Create a temporary dictionary and then add items from the provided dictionary.
            // Dictionary constructor does shallow copying by simply copying the provider name and provider reference pairs
            // in the provided customerProviders dictionary.
            Dictionary<string, SqlColumnEncryptionKeyStoreProvider> customColumnEncryptionKeyStoreProviders =
                new(customProviders, StringComparer.OrdinalIgnoreCase);

            _customColumnEncryptionKeyStoreProviders = customColumnEncryptionKeyStoreProviders;
        }

        private void ValidateCustomProviders(IDictionary<string, SqlColumnEncryptionKeyStoreProvider> customProviders)
        {
            // Throw when the provided dictionary is null.
            if (customProviders is null)
            {
                throw SQL.NullCustomKeyStoreProviderDictionary();
            }

            // Validate that custom provider list doesn't contain any of system provider list
            foreach (string key in customProviders.Keys)
            {
                // Validate the provider name
                //
                // Check for null or empty
                if (string.IsNullOrWhiteSpace(key))
                {
                    throw SQL.EmptyProviderName();
                }

                // Check if the name starts with MSSQL_, since this is reserved namespace for system providers.
                if (key.StartsWith(ADP.ColumnEncryptionSystemProviderNamePrefix, StringComparison.InvariantCultureIgnoreCase))
                {
                    throw SQL.InvalidCustomKeyStoreProviderName(key, ADP.ColumnEncryptionSystemProviderNamePrefix);
                }

                // Validate the provider value
                if (customProviders[key] is null)
                {
                    throw SQL.NullProviderValue(key);
                }
            }
        }

        /// <summary>
        /// This function walks through the registered custom column encryption key store providers and returns an object if found.
        /// </summary>
        /// <param name="providerName">Provider Name to be searched in custom provider dictionary.</param>
        /// <param name="columnKeyStoreProvider">If the provider is found, initializes the corresponding SqlColumnEncryptionKeyStoreProvider instance.</param>
        /// <returns>true if the provider is found, else returns false</returns>
        internal bool TryGetColumnEncryptionKeyStoreProvider(string providerName, out SqlColumnEncryptionKeyStoreProvider columnKeyStoreProvider)
        {
            Debug.Assert(!string.IsNullOrWhiteSpace(providerName), "Provider name is invalid");
            return _customColumnEncryptionKeyStoreProviders.TryGetValue(providerName, out columnKeyStoreProvider);
        }

        /// <summary>
        /// This function returns a list of the names of the custom providers currently registered.
        /// </summary>
        /// <returns>Combined list of provider names</returns>
        internal List<string> GetColumnEncryptionCustomKeyStoreProvidersNames()
        {
            if (_customColumnEncryptionKeyStoreProviders.Count > 0)
            {
                return new List<string>(_customColumnEncryptionKeyStoreProviders.Keys);
            }
            return new List<string>(0);
        }

        // If the user part is quoted, remove first and last brackets and then unquote any right square
        // brackets in the procedure.  This is a very simple parser that performs no validation.  As
        // with the function below, ideally we should have support from the server for this.
        private static string UnquoteProcedurePart(string part)
        {
            if (part != null && (2 <= part.Length))
            {
                if ('[' == part[0] && ']' == part[part.Length - 1])
                {
                    part = part.Substring(1, part.Length - 2); // strip outer '[' & ']'
                    part = part.Replace("]]", "]"); // undo quoted "]" from "]]" to "]"
                }
            }
            return part;
        }

        // User value in this format: [server].[database].[schema].[sp_foo];1
        // This function should only be passed "[sp_foo];1".
        // This function uses a pretty simple parser that doesn't do any validation.
        // Ideally, we would have support from the server rather than us having to do this.
        private static string UnquoteProcedureName(string name, out object groupNumber)
        {
            groupNumber = null; // Out param - initialize value to no value.
            string sproc = name;

            if (sproc != null)
            {
                if (char.IsDigit(sproc[sproc.Length - 1]))
                {
                    // If last char is a digit, parse.
                    int semicolon = sproc.LastIndexOf(';');
                    if (semicolon != -1)
                    {
                        // If we found a semicolon, obtain the integer.
                        string part = sproc.Substring(semicolon + 1);
                        int number = 0;
                        if (int.TryParse(part, out number))
                        {
                            // No checking, just fail if this doesn't work.
                            groupNumber = number;
                            sproc = sproc.Substring(0, semicolon);
                        }
                    }
                }
                sproc = UnquoteProcedurePart(sproc);
            }
            return sproc;
        }

        // Index into indirection arrays for columns of interest to DeriveParameters
        private enum ProcParamsColIndex
        {
            ParameterName = 0,
            ParameterType,
            DataType, // obsolete in 2008, use ManagedDataType instead
            ManagedDataType, // new in 2008
            CharacterMaximumLength,
            NumericPrecision,
            NumericScale,
            TypeCatalogName,
            TypeSchemaName,
            TypeName,
            XmlSchemaCollectionCatalogName,
            XmlSchemaCollectionSchemaName,
            XmlSchemaCollectionName,
            UdtTypeName, // obsolete in 2008.  Holds the actual typename if UDT, since TypeName didn't back then.
            DateTimeScale // new in 2008
        };

        // 2005- column ordinals (this array indexed by ProcParamsColIndex
        internal static readonly string[] PreSql2008ProcParamsNames = new string[] {
            "PARAMETER_NAME",           // ParameterName,
            "PARAMETER_TYPE",           // ParameterType,
            "DATA_TYPE",                // DataType
            null,                       // ManagedDataType,     introduced in 2008
            "CHARACTER_MAXIMUM_LENGTH", // CharacterMaximumLength,
            "NUMERIC_PRECISION",        // NumericPrecision,
            "NUMERIC_SCALE",            // NumericScale,
            "UDT_CATALOG",              // TypeCatalogName,
            "UDT_SCHEMA",               // TypeSchemaName,
            "TYPE_NAME",                // TypeName,
            "XML_CATALOGNAME",          // XmlSchemaCollectionCatalogName,
            "XML_SCHEMANAME",           // XmlSchemaCollectionSchemaName,
            "XML_SCHEMACOLLECTIONNAME", // XmlSchemaCollectionName
            "UDT_NAME",                 // UdtTypeName
            null,                       // Scale for datetime types with scale, introduced in 2008
        };

        // 2008+ column ordinals (this array indexed by ProcParamsColIndex
        internal static readonly string[] Sql2008ProcParamsNames = new string[] {
            "PARAMETER_NAME",           // ParameterName,
            "PARAMETER_TYPE",           // ParameterType,
            null,                       // DataType, removed from 2008+
            "MANAGED_DATA_TYPE",        // ManagedDataType,
            "CHARACTER_MAXIMUM_LENGTH", // CharacterMaximumLength,
            "NUMERIC_PRECISION",        // NumericPrecision,
            "NUMERIC_SCALE",            // NumericScale,
            "TYPE_CATALOG_NAME",        // TypeCatalogName,
            "TYPE_SCHEMA_NAME",         // TypeSchemaName,
            "TYPE_NAME",                // TypeName,
            "XML_CATALOGNAME",          // XmlSchemaCollectionCatalogName,
            "XML_SCHEMANAME",           // XmlSchemaCollectionSchemaName,
            "XML_SCHEMACOLLECTIONNAME", // XmlSchemaCollectionName
            null,                       // UdtTypeName, removed from 2008+
            "SS_DATETIME_PRECISION",    // Scale for datetime types with scale
        };

        internal void DeriveParameters()
        {
            switch (CommandType)
            {
                case CommandType.Text:
                    throw ADP.DeriveParametersNotSupported(this);
                case CommandType.StoredProcedure:
                    break;
                case CommandType.TableDirect:
                    // CommandType.TableDirect - do nothing, parameters are not supported
                    throw ADP.DeriveParametersNotSupported(this);
                default:
                    throw ADP.InvalidCommandType(CommandType);
            }

            // validate that we have a valid connection
            ValidateCommand(isAsync: false);

            // Use common parser for SqlClient and OleDb - parse into 4 parts - Server, Catalog, Schema, ProcedureName
            string[] parsedSProc = MultipartIdentifier.ParseMultipartIdentifier(CommandText, "[\"", "]\"", Strings.SQL_SqlCommandCommandText, false);
            if (string.IsNullOrEmpty(parsedSProc[3]))
            {
                throw ADP.NoStoredProcedureExists(CommandText);
            }

            Debug.Assert(parsedSProc.Length == 4, "Invalid array length result from SqlCommandBuilder.ParseProcedureName");

            SqlCommand paramsCmd = null;
            StringBuilder cmdText = new StringBuilder();

            // Build call for sp_procedure_params_rowset built of unquoted values from user:
            // [user server, if provided].[user catalog, else current database].[sys if 2005, else blank].[sp_procedure_params_rowset]

            // Server - pass only if user provided.
            if (!string.IsNullOrEmpty(parsedSProc[0]))
            {
                SqlCommandSet.BuildStoredProcedureName(cmdText, parsedSProc[0]);
                cmdText.Append(".");
            }

            // Catalog - pass user provided, otherwise use current database.
            if (string.IsNullOrEmpty(parsedSProc[1]))
            {
                parsedSProc[1] = Connection.Database;
            }
            SqlCommandSet.BuildStoredProcedureName(cmdText, parsedSProc[1]);
            cmdText.Append(".");

            // Schema - only if 2005, and then only pass sys.  Also - pass managed version of sproc
            // for 2005, else older sproc.
            string[] colNames;
            bool useManagedDataType;
            if (Connection.Is2008OrNewer)
            {
                // Procedure - [sp_procedure_params_managed]
                cmdText.Append("[sys].[").Append(TdsEnums.SP_PARAMS_MGD10).Append("]");

                colNames = Sql2008ProcParamsNames;
                useManagedDataType = true;
            }
            else
            {
                // Procedure - [sp_procedure_params_managed]
                cmdText.Append("[sys].[").Append(TdsEnums.SP_PARAMS_MANAGED).Append("]");

                colNames = PreSql2008ProcParamsNames;
                useManagedDataType = false;
            }


            paramsCmd = new SqlCommand(cmdText.ToString(), Connection, Transaction)
            {
                CommandType = CommandType.StoredProcedure
            };

            object groupNumber;

            // Prepare parameters for sp_procedure_params_rowset:
            // 1) procedure name - unquote user value
            // 2) group number - parsed at the time we unquoted procedure name
            // 3) procedure schema - unquote user value

            paramsCmd.Parameters.Add(new SqlParameter("@procedure_name", SqlDbType.NVarChar, 255));
            paramsCmd.Parameters[0].Value = UnquoteProcedureName(parsedSProc[3], out groupNumber); // ProcedureName is 4rd element in parsed array

            if (groupNumber != null)
            {
                SqlParameter param = paramsCmd.Parameters.Add(new SqlParameter("@group_number", SqlDbType.Int));
                param.Value = groupNumber;
            }

            if (!string.IsNullOrEmpty(parsedSProc[2]))
            {
                // SchemaName is 3rd element in parsed array
                SqlParameter param = paramsCmd.Parameters.Add(new SqlParameter("@procedure_schema", SqlDbType.NVarChar, 255));
                param.Value = UnquoteProcedurePart(parsedSProc[2]);
            }

            SqlDataReader r = null;

            List<SqlParameter> parameters = new List<SqlParameter>();
            bool processFinallyBlock = true;

            try
            {
                r = paramsCmd.ExecuteReader();

                SqlParameter p = null;

                while (r.Read())
                {
                    // each row corresponds to a parameter of the stored proc.  Fill in all the info
                    p = new SqlParameter()
                    {
                        ParameterName = (string)r[colNames[(int)ProcParamsColIndex.ParameterName]]
                    };

                    // type
                    if (useManagedDataType)
                    {
                        p.SqlDbType = (SqlDbType)(short)r[colNames[(int)ProcParamsColIndex.ManagedDataType]];

                        // 2005 didn't have as accurate of information as we're getting for 2008, so re-map a couple of
                        //  types for backward compatability.
                        switch (p.SqlDbType)
                        {
                            case SqlDbType.Image:
                            case SqlDbType.Timestamp:
                                p.SqlDbType = SqlDbType.VarBinary;
                                break;

                            case SqlDbType.NText:
                                p.SqlDbType = SqlDbType.NVarChar;
                                break;

                            case SqlDbType.Text:
                                p.SqlDbType = SqlDbType.VarChar;
                                break;

                            default:
                                break;
                        }
                    }
                    else
                    {
                        p.SqlDbType = MetaType.GetSqlDbTypeFromOleDbType((short)r[colNames[(int)ProcParamsColIndex.DataType]],
                            ADP.IsNull(r[colNames[(int)ProcParamsColIndex.TypeName]]) ? "" :
                                (string)r[colNames[(int)ProcParamsColIndex.TypeName]]);
                    }

                    // size
                    object a = r[colNames[(int)ProcParamsColIndex.CharacterMaximumLength]];
                    if (a is int)
                    {
                        int size = (int)a;

                        // Map MAX sizes correctly.  The 2008 server-side proc sends 0 for these instead of -1.
                        //  Should be fixed on the 2008 side, but would likely hold up the RI, and is safer to fix here.
                        //  If we can get the server-side fixed before shipping 2008, we can remove this mapping.
                        if (0 == size &&
                                (p.SqlDbType == SqlDbType.NVarChar ||
                                 p.SqlDbType == SqlDbType.VarBinary ||
                                 p.SqlDbType == SqlDbType.VarChar))
                        {
                            size = -1;
                        }
                        p.Size = size;
                    }

                    // direction
                    p.Direction = ParameterDirectionFromOleDbDirection((short)r[colNames[(int)ProcParamsColIndex.ParameterType]]);

                    if (p.SqlDbType == SqlDbType.Decimal)
                    {
                        p.ScaleInternal = (byte)((short)r[colNames[(int)ProcParamsColIndex.NumericScale]] & 0xff);
                        p.PrecisionInternal = (byte)((short)r[colNames[(int)ProcParamsColIndex.NumericPrecision]] & 0xff);
                    }

                    // type name for Udt
                    if (SqlDbType.Udt == p.SqlDbType)
                    {
                        string udtTypeName;
                        if (useManagedDataType)
                        {
                            udtTypeName = (string)r[colNames[(int)ProcParamsColIndex.TypeName]];
                        }
                        else
                        {
                            udtTypeName = (string)r[colNames[(int)ProcParamsColIndex.UdtTypeName]];
                        }

                        //read the type name
                        p.UdtTypeName = r[colNames[(int)ProcParamsColIndex.TypeCatalogName]] + "." +
                            r[colNames[(int)ProcParamsColIndex.TypeSchemaName]] + "." +
                            udtTypeName;
                    }

                    // type name for Structured types (same as for Udt's except assign p.TypeName instead of p.UdtTypeName
                    if (SqlDbType.Structured == p.SqlDbType)
                    {
                        Debug.Assert(_activeConnection.Is2008OrNewer, "Invalid datatype token received from pre-2008 server");

                        //read the type name
                        p.TypeName = r[colNames[(int)ProcParamsColIndex.TypeCatalogName]] + "." +
                            r[colNames[(int)ProcParamsColIndex.TypeSchemaName]] + "." +
                            r[colNames[(int)ProcParamsColIndex.TypeName]];

                        // the constructed type name above is incorrectly formatted, it should be a 2 part name not 3
                        // for compatibility we can't change this because the bug has existed for a long time and been 
                        // worked around by users, so identify that it is present and catch it later in the execution
                        // process once users can no longer interact with with the parameter type name
                        p.IsDerivedParameterTypeName = true;
                    }

                    // XmlSchema name for Xml types
                    if (SqlDbType.Xml == p.SqlDbType)
                    {
                        object value;

                        value = r[colNames[(int)ProcParamsColIndex.XmlSchemaCollectionCatalogName]];
                        p.XmlSchemaCollectionDatabase = ADP.IsNull(value) ? string.Empty : (string)value;

                        value = r[colNames[(int)ProcParamsColIndex.XmlSchemaCollectionSchemaName]];
                        p.XmlSchemaCollectionOwningSchema = ADP.IsNull(value) ? string.Empty : (string)value;

                        value = r[colNames[(int)ProcParamsColIndex.XmlSchemaCollectionName]];
                        p.XmlSchemaCollectionName = ADP.IsNull(value) ? string.Empty : (string)value;
                    }

                    if (MetaType._IsVarTime(p.SqlDbType))
                    {
                        object value = r[colNames[(int)ProcParamsColIndex.DateTimeScale]];
                        if (value is int)
                        {
                            p.ScaleInternal = (byte)(((int)value) & 0xff);
                        }
                    }

                    parameters.Add(p);
                }
            }
            catch (Exception e)
            {
                processFinallyBlock = ADP.IsCatchableExceptionType(e);
                throw;
            }
            finally
            {
                if (processFinallyBlock)
                {
                    r?.Close();

                    // always unhook the user's connection
                    paramsCmd.Connection = null;
                }
            }

            if (parameters.Count == 0)
            {
                throw ADP.NoStoredProcedureExists(this.CommandText);
            }

            Parameters.Clear();

            foreach (SqlParameter temp in parameters)
            {
                _parameters.Add(temp);
            }
        }

        private ParameterDirection ParameterDirectionFromOleDbDirection(short oledbDirection)
        {
            Debug.Assert(oledbDirection >= 1 && oledbDirection <= 4, "invalid parameter direction from params_rowset!");

            switch (oledbDirection)
            {
                case 2:
                    return ParameterDirection.InputOutput;
                case 3:
                    return ParameterDirection.Output;
                case 4:
                    return ParameterDirection.ReturnValue;
                default:
                    return ParameterDirection.Input;
            }

        }

        // get cached metadata
        internal _SqlMetaDataSet MetaData
        {
            get
            {
                return _cachedMetaData;
            }
        }

        // Check to see if notifications auto enlistment is turned on. Enlist if so.
        private void CheckNotificationStateAndAutoEnlist()
        {
            // First, if auto-enlist is on, check server version and then obtain context if
            // present.  If so, auto enlist to the dependency ID given in the context data.
            if (NotificationAutoEnlist)
            {
                string notifyContext = SqlNotificationContext();
                if (!string.IsNullOrEmpty(notifyContext))
                {
                    // Map to dependency by ID set in context data.
                    SqlDependency dependency = SqlDependencyPerAppDomainDispatcher.SingletonInstance.LookupDependencyEntry(notifyContext);

                    if (dependency != null)
                    {
                        // Add this command to the dependency.
                        dependency.AddCommandDependency(this);
                    }
                }
            }

            // If we have a notification with a dependency, setup the notification options at this time.

            // If user passes options, then we will always have option data at the time the SqlDependency
            // ctor is called.  But, if we are using default queue, then we do not have this data until
            // Start().  Due to this, we always delay setting options until execute.

            // There is a variance in order between Start(), SqlDependency(), and Execute.  This is the
            // best way to solve that problem.
            if (Notification != null)
            {
                if (_sqlDep != null)
                {
                    if (_sqlDep.Options == null)
                    {
                        // If null, SqlDependency was not created with options, so we need to obtain default options now.
                        // GetDefaultOptions can and will throw under certain conditions.

                        // In order to match to the appropriate start - we need 3 pieces of info:
                        // 1) server 2) user identity (SQL Auth or Int Sec) 3) database

                        SqlDependency.IdentityUserNamePair identityUserName = null;

                        // Obtain identity from connection.
                        SqlInternalConnectionTds internalConnection = _activeConnection.InnerConnection as SqlInternalConnectionTds;
                        if (internalConnection.Identity != null)
                        {
                            identityUserName = new SqlDependency.IdentityUserNamePair(internalConnection.Identity, null);
                        }
                        else
                        {
                            identityUserName = new SqlDependency.IdentityUserNamePair(null, internalConnection.ConnectionOptions.UserID);
                        }

                        Notification.Options = SqlDependency.GetDefaultComposedOptions(_activeConnection.DataSource,
                                                             InternalTdsConnection.ServerProvidedFailOverPartner,
                                                             identityUserName, _activeConnection.Database);
                    }

                    // Set UserData on notifications, as well as adding to the appdomain dispatcher.  The value is
                    // computed by an algorithm on the dependency - fixed and will always produce the same value
                    // given identical commandtext + parameter values.
                    Notification.UserData = _sqlDep.ComputeHashAndAddToDispatcher(this);
                    // Maintain server list for SqlDependency.
                    _sqlDep.AddToServerList(_activeConnection.DataSource);
                }
            }
        }

        [System.Security.Permissions.SecurityPermission(SecurityAction.Assert, Infrastructure = true)]
        static internal string SqlNotificationContext()
        {
            SqlConnection.VerifyExecutePermission();

            // since this information is protected, follow it so that it is not exposed to the user.
            // SQLBU 329633, SQLBU 329637
            return (System.Runtime.Remoting.Messaging.CallContext.GetData("MS.SqlDependencyCookie") as string);
        }

        // Tds-specific logic for ExecuteNonQuery run handling
        private Task RunExecuteNonQueryTds(string methodName, bool isAsync, int timeout, bool asyncWrite)
        {
            Debug.Assert(!asyncWrite || isAsync, "AsyncWrite should be always accompanied by Async");
            bool processFinallyBlock = true;
            try
            {
                Task reconnectTask = _activeConnection.ValidateAndReconnect(null, timeout);

                if (reconnectTask != null)
                {
                    long reconnectionStart = ADP.TimerCurrent();
                    if (isAsync)
                    {
                        TaskCompletionSource<object> completion = new TaskCompletionSource<object>();
                        _activeConnection.RegisterWaitingForReconnect(completion.Task);
                        _reconnectionCompletionSource = completion;
                        CancellationTokenSource timeoutCTS = new CancellationTokenSource();
                        AsyncHelper.SetTimeoutException(completion, timeout, static () => SQL.CR_ReconnectTimeout(), timeoutCTS.Token);
                        AsyncHelper.ContinueTask(reconnectTask, completion,
                            () =>
                            {
                                if (completion.Task.IsCompleted)
                                {
                                    return;
                                }
                                Interlocked.CompareExchange(ref _reconnectionCompletionSource, null, completion);
                                timeoutCTS.Cancel();
                                Task subTask = RunExecuteNonQueryTds(methodName, isAsync, TdsParserStaticMethods.GetRemainingTimeout(timeout, reconnectionStart), asyncWrite);
                                if (subTask == null)
                                {
                                    completion.SetResult(null);
                                }
                                else
                                {
                                    AsyncHelper.ContinueTaskWithState(subTask, completion, completion, static (object state) => ((TaskCompletionSource<object>)state).SetResult(null));
                                }
                            },
                            connectionToAbort: _activeConnection
                        );
                        return completion.Task;
                    }
                    else
                    {
                        AsyncHelper.WaitForCompletion(reconnectTask, timeout, static () => throw SQL.CR_ReconnectTimeout());
                        timeout = TdsParserStaticMethods.GetRemainingTimeout(timeout, reconnectionStart);
                    }
                }

                if (asyncWrite)
                {
                    _activeConnection.AddWeakReference(this, SqlReferenceCollection.CommandTag);
                }

                GetStateObject();

                // Reset the encryption state in case it has been set by a previous command.
                ResetEncryptionState();

                // we just send over the raw text with no annotation
                // no parameters are sent over
                // no data reader is returned
                // use this overload for "batch SQL" tds token type
                SqlClientEventSource.Log.TryTraceEvent("SqlCommand.RunExecuteNonQueryTds | Info | Object Id {0}, Activity Id {1}, Client Connection Id {2}, Command executed as SQLBATCH, Command Text '{3}' ", ObjectID, ActivityCorrelator.Current, Connection?.ClientConnectionId, CommandText);
                Task executeTask = _stateObj.Parser.TdsExecuteSQLBatch(this.CommandText, timeout, this.Notification, _stateObj, sync: true);
                Debug.Assert(executeTask == null, "Shouldn't get a task when doing sync writes");

                NotifyDependency();
                if (isAsync)
                {
                    _activeConnection.GetOpenTdsConnection(methodName).IncrementAsyncCount();
                }
                else
                {
                    Debug.Assert(_stateObj._syncOverAsync, "Should not attempt pends in a synchronous call");
                    TdsOperationStatus result = _stateObj.Parser.TryRun(RunBehavior.UntilDone, this, null, null, _stateObj, out _);
                    if (result != TdsOperationStatus.Done)
                    {
                        throw SQL.SynchronousCallMayNotPend();
                    }
                }
            }
            catch (Exception e)
            {
                processFinallyBlock = ADP.IsCatchableExceptionType(e);
                throw;
            }
            finally
            {
                if (processFinallyBlock && !isAsync)
                {
                    // When executing Async, we need to keep the _stateObj alive...
                    PutStateObject();
                }
            }
            return null;
        }

        /// <summary>
        /// Resets the encryption related state of the command object and each of the parameters.
        /// BatchRPC doesn't need special handling to cleanup the state of each RPC object and its parameters since a new RPC object and
        /// parameters are generated on every execution.
        /// </summary>
        private void ResetEncryptionState()
        {
            // First reset the command level state.
            ClearDescribeParameterEncryptionRequests();

            // Reset the state for internal End execution.
            _internalEndExecuteInitiated = false;

            // Reset the state for the cache.
            CachingQueryMetadataPostponed = false;

            // Reset the state of each of the parameters.
            if (_parameters != null)
            {
                for (int i = 0; i < _parameters.Count; i++)
                {
                    _parameters[i].CipherMetadata = null;
                    _parameters[i].HasReceivedMetadata = false;
                }
            }

            keysToBeSentToEnclave?.Clear();
            enclavePackage = null;
            requiresEnclaveComputations = false;
            enclaveAttestationParameters = null;
            customData = null;
            customDataLength = 0;
        }

        /// <summary>
        /// Steps to be executed in the Prepare Transparent Encryption finally block.
        /// </summary>
        private void PrepareTransparentEncryptionFinallyBlock(bool closeDataReader,
            bool clearDataStructures,
            bool decrementAsyncCount,
            bool wasDescribeParameterEncryptionNeeded,
            ReadOnlyDictionary<_SqlRPC, _SqlRPC> describeParameterEncryptionRpcOriginalRpcMap,
            SqlDataReader describeParameterEncryptionDataReader)
        {
            if (clearDataStructures)
            {
                // Clear some state variables in SqlCommand that reflect in-progress describe parameter encryption requests.
                ClearDescribeParameterEncryptionRequests();

                if (describeParameterEncryptionRpcOriginalRpcMap != null)
                {
                    describeParameterEncryptionRpcOriginalRpcMap = null;
                }
            }

            // Decrement the async count.
            if (decrementAsyncCount)
            {
                SqlInternalConnectionTds internalConnectionTds = _activeConnection.GetOpenTdsConnection();
                if (internalConnectionTds != null)
                {
                    internalConnectionTds.DecrementAsyncCount();
                }
            }

            if (closeDataReader)
            {
                // Close the data reader to reset the _stateObj
                if (describeParameterEncryptionDataReader != null)
                {
                    describeParameterEncryptionDataReader.Close();
                }
            }
        }

        /// <summary>
        /// Executes the reader after checking to see if we need to encrypt input parameters and then encrypting it if required.
        /// TryFetchInputParameterEncryptionInfo() -> ReadDescribeEncryptionParameterResults()-> EncryptInputParameters() ->RunExecuteReaderTds()
        /// </summary>
        /// <param name="isAsync"></param>
        /// <param name="timeout"></param>
        /// <param name="completion"></param>
        /// <param name="returnTask"></param>
        /// <param name="asyncWrite"></param>
        /// <param name="usedCache"></param>
        /// <param name="isRetry"></param>
        /// <returns></returns>
        private void PrepareForTransparentEncryption(
            bool isAsync,
            int timeout,
            TaskCompletionSource<object> completion,
            out Task returnTask,
            bool asyncWrite,
            out bool usedCache,
            bool isRetry)
        {
            // Fetch reader with input params
            Task fetchInputParameterEncryptionInfoTask = null;
            bool describeParameterEncryptionNeeded = false;
            SqlDataReader describeParameterEncryptionDataReader = null;
            returnTask = null;
            usedCache = false;

            Debug.Assert(_activeConnection != null, "_activeConnection should not be null in PrepareForTransparentEncryption.");
            Debug.Assert(_activeConnection.Parser != null, "_activeConnection.Parser should not be null in PrepareForTransparentEncryption.");
            Debug.Assert(_activeConnection.Parser.IsColumnEncryptionSupported,
                "_activeConnection.Parser.IsColumnEncryptionSupported should be true in PrepareForTransparentEncryption.");
            Debug.Assert(_columnEncryptionSetting == SqlCommandColumnEncryptionSetting.Enabled
                        || (_columnEncryptionSetting == SqlCommandColumnEncryptionSetting.UseConnectionSetting && _activeConnection.IsColumnEncryptionSettingEnabled),
                        "ColumnEncryption setting should be enabled for input parameter encryption.");
            Debug.Assert(isAsync == (completion != null), "completion should can be null if and only if mode is async.");

            // If we are not in Batch RPC and not already retrying, attempt to fetch the cipher MD for each parameter from the cache.
            // If this succeeds then return immediately, otherwise just fall back to the full crypto MD discovery.
            if (!_batchRPCMode && !isRetry && (this._parameters != null && this._parameters.Count > 0) && SqlQueryMetadataCache.GetInstance().GetQueryMetadataIfExists(this))
            {
                usedCache = true;
                return;
            }

            // A flag to indicate if finallyblock needs to execute.
            bool processFinallyBlock = true;

            // A flag to indicate if we need to decrement async count on the connection in finally block.
            bool decrementAsyncCountInFinallyBlock = false;

            // Flag to indicate if exception is caught during the execution, to govern clean up.
            bool exceptionCaught = false;

            // Used in _batchRPCMode to maintain a map of describe parameter encryption RPC requests (Keys) and their corresponding original RPC requests (Values).
            ReadOnlyDictionary<_SqlRPC, _SqlRPC> describeParameterEncryptionRpcOriginalRpcMap = null;

            TdsParser bestEffortCleanupTarget = null;
            RuntimeHelpers.PrepareConstrainedRegions();
            try
            {
                bestEffortCleanupTarget = SqlInternalConnection.GetBestEffortCleanupTarget(_activeConnection);
                try
                {
                    // Fetch the encryption information that applies to any of the input parameters.
                    describeParameterEncryptionDataReader = TryFetchInputParameterEncryptionInfo(timeout,
                                                                                                 isAsync,
                                                                                                 asyncWrite,
                                                                                                 out describeParameterEncryptionNeeded,
                                                                                                 out fetchInputParameterEncryptionInfoTask,
                                                                                                 out describeParameterEncryptionRpcOriginalRpcMap,
                                                                                                 isRetry);

                    Debug.Assert(describeParameterEncryptionNeeded || describeParameterEncryptionDataReader == null,
                        "describeParameterEncryptionDataReader should be null if we don't need to request describe parameter encryption request.");

                    Debug.Assert(fetchInputParameterEncryptionInfoTask == null || isAsync,
                        "Task returned by TryFetchInputParameterEncryptionInfo, when in sync mode, in PrepareForTransparentEncryption.");

                    Debug.Assert((describeParameterEncryptionRpcOriginalRpcMap != null) == _batchRPCMode,
                        "describeParameterEncryptionRpcOriginalRpcMap can be non-null if and only if it is in _batchRPCMode.");

                    // If we didn't have parameters, we can fall back to regular code path, by simply returning.
                    if (!describeParameterEncryptionNeeded)
                    {
                        Debug.Assert(fetchInputParameterEncryptionInfoTask == null,
                            "fetchInputParameterEncryptionInfoTask should not be set if describe parameter encryption is not needed.");

                        Debug.Assert(describeParameterEncryptionDataReader == null,
                            "SqlDataReader created for describe parameter encryption params when it is not needed.");

                        return;
                    }

                    // If we are in async execution, we need to decrement our async count on exception.
                    decrementAsyncCountInFinallyBlock = isAsync;

                    Debug.Assert(describeParameterEncryptionDataReader != null,
                        "describeParameterEncryptionDataReader should not be null, as it is required to get results of describe parameter encryption.");

                    // Fire up another task to read the results of describe parameter encryption
                    if (fetchInputParameterEncryptionInfoTask != null)
                    {
                        // Mark that we should not process the finally block since we have async execution pending.
                        // Note that this should be done outside the task's continuation delegate.
                        processFinallyBlock = false;
                        returnTask = AsyncHelper.CreateContinuationTask(fetchInputParameterEncryptionInfoTask, () =>
                        {
                            bool processFinallyBlockAsync = true;
                            bool decrementAsyncCountInFinallyBlockAsync = true;

                            RuntimeHelpers.PrepareConstrainedRegions();
                            try
                            {
                                // Check for any exceptions on network write, before reading.
                                CheckThrowSNIException();

                                // If it is async, then TryFetchInputParameterEncryptionInfo-> RunExecuteReaderTds would have incremented the async count.
                                // Decrement it when we are about to complete async execute reader.
                                SqlInternalConnectionTds internalConnectionTds = _activeConnection.GetOpenTdsConnection();
                                if (internalConnectionTds != null)
                                {
                                    internalConnectionTds.DecrementAsyncCount();
                                    decrementAsyncCountInFinallyBlockAsync = false;
                                }

                                // Complete executereader.
                                describeParameterEncryptionDataReader = CompleteAsyncExecuteReader(forDescribeParameterEncryption: true);
                                Debug.Assert(_stateObj == null, "non-null state object in PrepareForTransparentEncryption.");

                                // Read the results of describe parameter encryption.
                                ReadDescribeEncryptionParameterResults(
                                    describeParameterEncryptionDataReader,
                                    describeParameterEncryptionRpcOriginalRpcMap,
                                    isRetry);

#if DEBUG
                                // Failpoint to force the thread to halt to simulate cancellation of SqlCommand.
                                if (_sleepAfterReadDescribeEncryptionParameterResults)
                                {
                                    Thread.Sleep(10000);
                                }
#endif //DEBUG
                            }
                            catch (Exception e)
                            {
                                processFinallyBlockAsync = ADP.IsCatchableExceptionType(e);
                                throw;
                            }
                            finally
                            {
                                PrepareTransparentEncryptionFinallyBlock(closeDataReader: processFinallyBlockAsync,
                                                                            decrementAsyncCount: decrementAsyncCountInFinallyBlockAsync,
                                                                            clearDataStructures: processFinallyBlockAsync,
                                                                            wasDescribeParameterEncryptionNeeded: describeParameterEncryptionNeeded,
                                                                            describeParameterEncryptionRpcOriginalRpcMap: describeParameterEncryptionRpcOriginalRpcMap,
                                                                            describeParameterEncryptionDataReader: describeParameterEncryptionDataReader);
                            }
                        },
                        onFailure: ((exception) =>
                        {
                            if (CachedAsyncState != null)
                            {
                                CachedAsyncState.ResetAsyncState();
                            }
                            if (exception != null)
                            {
                                throw exception;
                            }
                        }));

                        decrementAsyncCountInFinallyBlock = false;
                    }
                    else
                    {
                        // If it was async, ending the reader is still pending.
                        if (isAsync)
                        {
                            // Mark that we should not process the finally block since we have async execution pending.
                            // Note that this should be done outside the task's continuation delegate.
                            processFinallyBlock = false;
                            returnTask = Task.Run(() =>
                            {
                                bool processFinallyBlockAsync = true;
                                bool decrementAsyncCountInFinallyBlockAsync = true;

                                RuntimeHelpers.PrepareConstrainedRegions();
                                try
                                {

                                    // Check for any exceptions on network write, before reading.
                                    CheckThrowSNIException();

                                    // If it is async, then TryFetchInputParameterEncryptionInfo-> RunExecuteReaderTds would have incremented the async count.
                                    // Decrement it when we are about to complete async execute reader.
                                    SqlInternalConnectionTds internalConnectionTds = _activeConnection.GetOpenTdsConnection();
                                    if (internalConnectionTds != null)
                                    {
                                        internalConnectionTds.DecrementAsyncCount();
                                        decrementAsyncCountInFinallyBlockAsync = false;
                                    }

                                    // Complete executereader.
                                    describeParameterEncryptionDataReader = CompleteAsyncExecuteReader(forDescribeParameterEncryption: true);
                                    Debug.Assert(_stateObj == null, "non-null state object in PrepareForTransparentEncryption.");

                                    // Read the results of describe parameter encryption.
                                    ReadDescribeEncryptionParameterResults(describeParameterEncryptionDataReader, describeParameterEncryptionRpcOriginalRpcMap, isRetry);
#if DEBUG
                                    // Failpoint to force the thread to halt to simulate cancellation of SqlCommand.
                                    if (_sleepAfterReadDescribeEncryptionParameterResults)
                                    {
                                        Thread.Sleep(10000);
                                    }
#endif
                                }
                                catch (Exception e)
                                {
                                    processFinallyBlockAsync = ADP.IsCatchableExceptionType(e);
                                    throw;
                                }
                                finally
                                {
                                    PrepareTransparentEncryptionFinallyBlock(closeDataReader: processFinallyBlockAsync,
                                                                                decrementAsyncCount: decrementAsyncCountInFinallyBlockAsync,
                                                                                clearDataStructures: processFinallyBlockAsync,
                                                                                wasDescribeParameterEncryptionNeeded: describeParameterEncryptionNeeded,
                                                                                describeParameterEncryptionRpcOriginalRpcMap: describeParameterEncryptionRpcOriginalRpcMap,
                                                                                describeParameterEncryptionDataReader: describeParameterEncryptionDataReader);
                                }
                            });

                            decrementAsyncCountInFinallyBlock = false;
                        }
                        else
                        {
                            // For synchronous execution, read the results of describe parameter encryption here.
                            ReadDescribeEncryptionParameterResults(describeParameterEncryptionDataReader, describeParameterEncryptionRpcOriginalRpcMap, isRetry);
                        }

#if DEBUG
                        // Failpoint to force the thread to halt to simulate cancellation of SqlCommand.
                        if (_sleepAfterReadDescribeEncryptionParameterResults)
                        {
                            Thread.Sleep(10000);
                        }
#endif
                    }
                }
                catch (Exception e)
                {
                    processFinallyBlock = ADP.IsCatchableExceptionType(e);
                    exceptionCaught = true;
                    throw;
                }
                finally
                {
                    // Free up the state only for synchronous execution. For asynchronous execution, free only if there was an exception.
                    PrepareTransparentEncryptionFinallyBlock(closeDataReader: (processFinallyBlock && !isAsync) || exceptionCaught,
                                           decrementAsyncCount: decrementAsyncCountInFinallyBlock && exceptionCaught,
                                           clearDataStructures: (processFinallyBlock && !isAsync) || exceptionCaught,
                                           wasDescribeParameterEncryptionNeeded: describeParameterEncryptionNeeded,
                                           describeParameterEncryptionRpcOriginalRpcMap: describeParameterEncryptionRpcOriginalRpcMap,
                                           describeParameterEncryptionDataReader: describeParameterEncryptionDataReader);
                }
            }
            catch (System.OutOfMemoryException e)
            {
                _activeConnection.Abort(e);
                throw;
            }
            catch (System.StackOverflowException e)
            {
                _activeConnection.Abort(e);
                throw;
            }
            catch (System.Threading.ThreadAbortException e)
            {
                _activeConnection.Abort(e);
                SqlInternalConnection.BestEffortCleanup(bestEffortCleanupTarget);
                throw;
            }
            catch (Exception e)
            {
                if (CachedAsyncState != null)
                {
                    CachedAsyncState.ResetAsyncState();
                }

                if (ADP.IsCatchableExceptionType(e))
                {
                    ReliablePutStateObject();
                }

                throw;
            }
        }

        /// <summary>
        /// Executes an RPC to fetch param encryption info from SQL Engine. If this method is not done writing
        ///  the request to wire, it'll set the "task" parameter which can be used to create continuations.
        /// </summary>
        /// <param name="timeout"></param>
        /// <param name="isAsync"></param>
        /// <param name="asyncWrite"></param>
        /// <param name="inputParameterEncryptionNeeded"></param>
        /// <param name="task"></param>
        /// <param name="describeParameterEncryptionRpcOriginalRpcMap"></param>
        /// <param name="isRetry">Indicates if this is a retry from a failed call.</param>
        /// <returns></returns>
        private SqlDataReader TryFetchInputParameterEncryptionInfo(
            int timeout,
            bool isAsync,
            bool asyncWrite,
            out bool inputParameterEncryptionNeeded,
            out Task task,
            out ReadOnlyDictionary<_SqlRPC, _SqlRPC> describeParameterEncryptionRpcOriginalRpcMap,
            bool isRetry)
        {
            inputParameterEncryptionNeeded = false;
            task = null;
            describeParameterEncryptionRpcOriginalRpcMap = null;
            byte[] serializedAttestationParameters = null;

            if (ShouldUseEnclaveBasedWorkflow)
            {
                SqlConnectionAttestationProtocol attestationProtocol = this._activeConnection.AttestationProtocol;
                string enclaveType = this._activeConnection.Parser.EnclaveType;

                EnclaveSessionParameters enclaveSessionParameters = GetEnclaveSessionParameters();

                SqlEnclaveSession sqlEnclaveSession = null;
                EnclaveDelegate.Instance.GetEnclaveSession(attestationProtocol, enclaveType, enclaveSessionParameters, true, isRetry, out sqlEnclaveSession, out customData, out customDataLength);
                if (sqlEnclaveSession == null)
                {
                    enclaveAttestationParameters = EnclaveDelegate.Instance.GetAttestationParameters(attestationProtocol, enclaveType, enclaveSessionParameters.AttestationUrl, customData, customDataLength);
                    serializedAttestationParameters = EnclaveDelegate.Instance.GetSerializedAttestationParameters(enclaveAttestationParameters, enclaveType);
                }
            }

            if (_batchRPCMode)
            {
                // Count the rpc requests that need to be transparently encrypted
                // We simply look for any parameters in a request and add the request to be queried for parameter encryption
                Dictionary<_SqlRPC, _SqlRPC> describeParameterEncryptionRpcOriginalRpcDictionary = new Dictionary<_SqlRPC, _SqlRPC>();

                for (int i = 0; i < _RPCList.Count; i++)
                {
                    // In BatchRPCMode, the actual T-SQL query is in the first parameter and not present as the rpcName, as is the case with non-BatchRPCMode.
                    // So input parameters start at parameters[1]. parameters[0] is the actual T-SQL Statement. rpcName is sp_executesql.
                    if (_RPCList[i].systemParams.Length > 1)
                    {
                        _RPCList[i].needsFetchParameterEncryptionMetadata = true;

                        // Since we are going to need multiple RPC objects, allocate a new one here for each command in the batch.
                        _SqlRPC rpcDescribeParameterEncryptionRequest = new _SqlRPC();

                        // Prepare the describe parameter encryption request.
                        PrepareDescribeParameterEncryptionRequest(_RPCList[i], ref rpcDescribeParameterEncryptionRequest, i == 0 ? serializedAttestationParameters : null);
                        Debug.Assert(rpcDescribeParameterEncryptionRequest != null, "rpcDescribeParameterEncryptionRequest should not be null, after call to PrepareDescribeParameterEncryptionRequest.");

                        Debug.Assert(!describeParameterEncryptionRpcOriginalRpcDictionary.ContainsKey(rpcDescribeParameterEncryptionRequest),
                            "There should not already be a key referring to the current rpcDescribeParameterEncryptionRequest, in the dictionary describeParameterEncryptionRpcOriginalRpcDictionary.");

                        // Add the describe parameter encryption RPC request as the key and its corresponding original rpc request to the dictionary.
                        describeParameterEncryptionRpcOriginalRpcDictionary.Add(rpcDescribeParameterEncryptionRequest, _RPCList[i]);
                    }
                }

                describeParameterEncryptionRpcOriginalRpcMap = new ReadOnlyDictionary<_SqlRPC, _SqlRPC>(describeParameterEncryptionRpcOriginalRpcDictionary);

                if (describeParameterEncryptionRpcOriginalRpcMap.Count == 0)
                {
                    // If no parameters are present, nothing to do, simply return.
                    return null;
                }
                else
                {
                    inputParameterEncryptionNeeded = true;
                }

                _sqlRPCParameterEncryptionReqArray = new _SqlRPC[describeParameterEncryptionRpcOriginalRpcMap.Count];
                describeParameterEncryptionRpcOriginalRpcMap.Keys.CopyTo(_sqlRPCParameterEncryptionReqArray, 0);

                Debug.Assert(_sqlRPCParameterEncryptionReqArray.Length > 0, "There should be at-least 1 describe parameter encryption rpc request.");
                Debug.Assert(_sqlRPCParameterEncryptionReqArray.Length <= _RPCList.Count,
                                "The number of decribe parameter encryption RPC requests is more than the number of original RPC requests.");
            }
            //Always Encrypted generally operates only on parameterized queries. However enclave based Always encrypted also supports unparameterized queries
            else if (ShouldUseEnclaveBasedWorkflow || (0 != GetParameterCount(_parameters)))
            {
                // Fetch params for a single batch
                inputParameterEncryptionNeeded = true;
                _sqlRPCParameterEncryptionReqArray = new _SqlRPC[1];

                _SqlRPC rpc = null;
                GetRPCObject(0, GetParameterCount(_parameters), ref rpc);
                Debug.Assert(rpc != null, "GetRPCObject should not return rpc as null.");

                rpc.rpcName = CommandText;
                rpc.userParams = _parameters;

                // Prepare the RPC request for describe parameter encryption procedure.
                PrepareDescribeParameterEncryptionRequest(rpc, ref _sqlRPCParameterEncryptionReqArray[0], serializedAttestationParameters);
                Debug.Assert(_sqlRPCParameterEncryptionReqArray[0] != null, "_sqlRPCParameterEncryptionReqArray[0] should not be null, after call to PrepareDescribeParameterEncryptionRequest.");
            }

            if (inputParameterEncryptionNeeded)
            {
                // Set the flag that indicates that parameter encryption requests are currently in-progress.
                IsDescribeParameterEncryptionRPCCurrentlyInProgress = true;

#if DEBUG
                // Failpoint to force the thread to halt to simulate cancellation of SqlCommand.
                if (_sleepDuringTryFetchInputParameterEncryptionInfo)
                {
                    Thread.Sleep(10000);
                }
#endif

                // Execute the RPC.
                return RunExecuteReaderTds(
                    CommandBehavior.Default,
                    runBehavior: RunBehavior.ReturnImmediately,
                    returnStream: true,
                    isAsync: isAsync,
                    timeout: timeout,
                    task: out task,
                    asyncWrite: asyncWrite,
                    isRetry: false,
                    ds: null,
                    describeParameterEncryptionRequest: true);
            }
            else
            {
                return null;
            }
        }

        /// <summary>
        /// Constructs a SqlParameter with a given string value
        /// </summary>
        /// <param name="queryText"></param>
        /// <returns></returns>
        private SqlParameter GetSqlParameterWithQueryText(string queryText)
        {
            SqlParameter sqlParam = new SqlParameter(null, ((queryText.Length << 1) <= TdsEnums.TYPE_SIZE_LIMIT) ? SqlDbType.NVarChar : SqlDbType.NText, queryText.Length);
            sqlParam.Value = queryText;

            return sqlParam;
        }

        /// <summary>
        /// Constructs the sp_describe_parameter_encryption request with the values from the original RPC call.	
        /// Prototype for &lt;sp_describe_parameter_encryption&gt; is 	
        /// exec sp_describe_parameter_encryption @tsql=N'[SQL Statement]', @params=N'@p1 varbinary(256)'
        /// </summary>
        /// <param name="originalRpcRequest"></param>
        /// <param name="describeParameterEncryptionRequest"></param>
        /// <param name="attestationParameters"></param>
        private void PrepareDescribeParameterEncryptionRequest(_SqlRPC originalRpcRequest, ref _SqlRPC describeParameterEncryptionRequest, byte[] attestationParameters = null)
        {
            Debug.Assert(originalRpcRequest != null);

            // Construct the RPC request for sp_describe_parameter_encryption
            // sp_describe_parameter_encryption always has 2 parameters (stmt, paramlist).
            // sp_describe_parameter_encryption can have an optional 3rd parameter (attestationParameters), used to identify and execute attestation protocol
            GetRPCObject(attestationParameters == null ? 2 : 3, 0, ref describeParameterEncryptionRequest, forSpDescribeParameterEncryption: true);
            describeParameterEncryptionRequest.rpcName = "sp_describe_parameter_encryption";

            // Prepare @tsql parameter
            string text;

            // In _batchRPCMode, The actual T-SQL query is in the first parameter and not present as the rpcName, as is the case with non-_batchRPCMode.
            if (_batchRPCMode)
            {
                Debug.Assert(originalRpcRequest.systemParamCount > 0,
                    "originalRpcRequest didn't have at-least 1 parameter in BatchRPCMode, in PrepareDescribeParameterEncryptionRequest.");
                text = (string)originalRpcRequest.systemParams[0].Value;
                //@tsql
                SqlParameter tsqlParam = describeParameterEncryptionRequest.systemParams[0];
                tsqlParam.SqlDbType = ((text.Length << 1) <= TdsEnums.TYPE_SIZE_LIMIT) ? SqlDbType.NVarChar : SqlDbType.NText;
                tsqlParam.Value = text;
                tsqlParam.Size = text.Length;
                tsqlParam.Direction = ParameterDirection.Input;
            }
            else
            {
                text = originalRpcRequest.rpcName;
                if (CommandType == CommandType.StoredProcedure)
                {
                    // For stored procedures, we need to prepare @tsql in the following format
                    // N'EXEC sp_name @param1=@param1, @param1=@param2, ..., @paramN=@paramN'
                    describeParameterEncryptionRequest.systemParams[0] = BuildStoredProcedureStatementForColumnEncryption(text, originalRpcRequest.userParams);
                }
                else
                {
                    //@tsql
                    SqlParameter tsqlParam = describeParameterEncryptionRequest.systemParams[0];
                    tsqlParam.SqlDbType = ((text.Length << 1) <= TdsEnums.TYPE_SIZE_LIMIT) ? SqlDbType.NVarChar : SqlDbType.NText;
                    tsqlParam.Value = text;
                    tsqlParam.Size = text.Length;
                    tsqlParam.Direction = ParameterDirection.Input;
                }
            }

            Debug.Assert(text != null, "@tsql parameter is null in PrepareDescribeParameterEncryptionRequest.");
            string parameterList = null;

            // In BatchRPCMode, the input parameters start at parameters[1]. parameters[0] is the T-SQL statement. rpcName is sp_executesql.
            // And it is already in the format expected out of BuildParamList, which is not the case with Non-BatchRPCMode.
            if (_batchRPCMode)
            {
                // systemParamCount == 2 when user parameters are supplied to BuildExecuteSql
                if (originalRpcRequest.systemParamCount > 1)
                {
                    parameterList = (string)originalRpcRequest.systemParams[1].Value;
                }
            }
            else
            {
                // Prepare @params parameter
                // Need to create new parameters as we cannot have the same parameter being part of two SqlCommand objects
                SqlParameterCollection tempCollection = new SqlParameterCollection();

                if (originalRpcRequest.userParams != null)
                {
                    for (int i = 0; i < originalRpcRequest.userParams.Count; i++)
                    {
                        SqlParameter param = originalRpcRequest.userParams[i];
                        SqlParameter paramCopy = new SqlParameter(
                            param.ParameterName,
                            param.SqlDbType,
                            param.Size,
                            param.Direction,
                            param.Precision,
                            param.Scale,
                            param.SourceColumn,
                            param.SourceVersion,
                            param.SourceColumnNullMapping,
                            param.Value,
                            param.XmlSchemaCollectionDatabase,
                            param.XmlSchemaCollectionOwningSchema,
                            param.XmlSchemaCollectionName
                        );
                        paramCopy.CompareInfo = param.CompareInfo;
                        paramCopy.TypeName = param.TypeName;
                        paramCopy.UdtTypeName = param.UdtTypeName;
                        paramCopy.IsNullable = param.IsNullable;
                        paramCopy.LocaleId = param.LocaleId;
                        paramCopy.Offset = param.Offset;

                        tempCollection.Add(paramCopy);
                    }
                }

                Debug.Assert(_stateObj == null, "_stateObj should be null at this time, in PrepareDescribeParameterEncryptionRequest.");
                Debug.Assert(_activeConnection != null, "_activeConnection should not be null at this time, in PrepareDescribeParameterEncryptionRequest.");
                TdsParser tdsParser = null;

                if (_activeConnection.Parser != null)
                {
                    tdsParser = _activeConnection.Parser;
                    if ((tdsParser == null) || (tdsParser.State == TdsParserState.Broken) || (tdsParser.State == TdsParserState.Closed))
                    {
                        // Connection's parser is null as well, therefore we must be closed
                        throw ADP.ClosedConnectionError();
                    }
                }

                parameterList = BuildParamList(tdsParser, tempCollection, includeReturnValue: true);
            }

            SqlParameter paramsParam = describeParameterEncryptionRequest.systemParams[1];
            paramsParam.SqlDbType = ((parameterList.Length << 1) <= TdsEnums.TYPE_SIZE_LIMIT) ? SqlDbType.NVarChar : SqlDbType.NText;
            paramsParam.Size = parameterList.Length;
            paramsParam.Value = parameterList;
            paramsParam.Direction = ParameterDirection.Input;

            if (attestationParameters != null)
            {
                SqlParameter attestationParametersParam = describeParameterEncryptionRequest.systemParams[2];
                attestationParametersParam.SqlDbType = SqlDbType.VarBinary;
                attestationParametersParam.Size = attestationParameters.Length;
                attestationParametersParam.Value = attestationParameters;
                attestationParametersParam.Direction = ParameterDirection.Input;
            }
        }

        /// <summary>
        /// Read the output of sp_describe_parameter_encryption
        /// </summary>
        /// <param name="ds">Resultset from calling to sp_describe_parameter_encryption</param>
        /// <param name="describeParameterEncryptionRpcOriginalRpcMap"> Readonly dictionary with the map of parameter encryption rpc requests with the corresponding original rpc requests.</param>
        /// <param name="isRetry">Indicates if this is a retry from a failed call.</param>
        private void ReadDescribeEncryptionParameterResults(
            SqlDataReader ds,
            ReadOnlyDictionary<_SqlRPC, _SqlRPC> describeParameterEncryptionRpcOriginalRpcMap,
            bool isRetry)
        {
            _SqlRPC rpc = null;
            int currentOrdinal = -1;
            SqlTceCipherInfoEntry cipherInfoEntry;
            Dictionary<int, SqlTceCipherInfoEntry> columnEncryptionKeyTable = new Dictionary<int, SqlTceCipherInfoEntry>();

            Debug.Assert((describeParameterEncryptionRpcOriginalRpcMap != null) == _batchRPCMode,
                "describeParameterEncryptionRpcOriginalRpcMap should be non-null if and only if it is _batchRPCMode.");

            // Indicates the current result set we are reading, used in BatchRPCMode, where we can have more than 1 result set.
            int resultSetSequenceNumber = 0;

#if DEBUG
            // Keep track of the number of rows in the result sets.
            int rowsAffected = 0;
#endif

            // A flag that used in BatchRPCMode, to assert the result of lookup in to the dictionary maintaining the map of describe parameter encryption requests
            // and the corresponding original rpc requests.
            bool lookupDictionaryResult;

            do
            {
                if (_batchRPCMode)
                {
                    // If we got more RPC results from the server than what was requested.
                    if (resultSetSequenceNumber >= _sqlRPCParameterEncryptionReqArray.Length)
                    {
                        Debug.Assert(false, "Server sent back more results than what was expected for describe parameter encryption requests in _batchRPCMode.");
                        // Ignore the rest of the results from the server, if for whatever reason it sends back more than what we expect.
                        break;
                    }
                }

                bool enclaveMetadataExists = true;

                // First read the column encryption key list
                while (ds.Read())
                {

#if DEBUG
                    rowsAffected++;
#endif

                    // Column Encryption Key Ordinal.
                    currentOrdinal = ds.GetInt32((int)DescribeParameterEncryptionResultSet1.KeyOrdinal);
                    Debug.Assert(currentOrdinal >= 0, "currentOrdinal cannot be negative.");

                    // Try to see if there was already an entry for the current ordinal.
                    if (!columnEncryptionKeyTable.TryGetValue(currentOrdinal, out cipherInfoEntry))
                    {
                        // If an entry for this ordinal was not found, create an entry in the columnEncryptionKeyTable for this ordinal.
                        cipherInfoEntry = new SqlTceCipherInfoEntry(currentOrdinal);
                        columnEncryptionKeyTable.Add(currentOrdinal, cipherInfoEntry);
                    }

                    Debug.Assert(!cipherInfoEntry.Equals(default(SqlTceCipherInfoEntry)), "cipherInfoEntry should not be un-initialized.");

                    // Read the CEK.
                    byte[] encryptedKey = null;
                    int encryptedKeyLength = (int)ds.GetBytes((int)DescribeParameterEncryptionResultSet1.EncryptedKey, 0, encryptedKey, 0, 0);
                    encryptedKey = new byte[encryptedKeyLength];
                    ds.GetBytes((int)DescribeParameterEncryptionResultSet1.EncryptedKey, 0, encryptedKey, 0, encryptedKeyLength);

                    // Read the metadata version of the key.
                    // It should always be 8 bytes.
                    byte[] keyMdVersion = new byte[8];
                    ds.GetBytes((int)DescribeParameterEncryptionResultSet1.KeyMdVersion, 0, keyMdVersion, 0, keyMdVersion.Length);

                    // Validate the provider name
                    string providerName = ds.GetString((int)DescribeParameterEncryptionResultSet1.ProviderName);

                    string keyPath = ds.GetString((int)DescribeParameterEncryptionResultSet1.KeyPath);
                    cipherInfoEntry.Add(encryptedKey: encryptedKey,
                                        databaseId: ds.GetInt32((int)DescribeParameterEncryptionResultSet1.DbId),
                                        cekId: ds.GetInt32((int)DescribeParameterEncryptionResultSet1.KeyId),
                                        cekVersion: ds.GetInt32((int)DescribeParameterEncryptionResultSet1.KeyVersion),
                                        cekMdVersion: keyMdVersion,
                                        keyPath: keyPath,
                                        keyStoreName: providerName,
                                        algorithmName: ds.GetString((int)DescribeParameterEncryptionResultSet1.KeyEncryptionAlgorithm));

                    bool isRequestedByEnclave = false;

                    // Servers supporting enclave computations should always
                    // return a boolean indicating whether the key is required by enclave or not.
                    if (this._activeConnection.Parser.TceVersionSupported >= TdsEnums.MIN_TCE_VERSION_WITH_ENCLAVE_SUPPORT)
                    {
                        isRequestedByEnclave =
                            ds.GetBoolean((int)DescribeParameterEncryptionResultSet1.IsRequestedByEnclave);
                    }
                    else
                    {
                        enclaveMetadataExists = false;
                    }

                    if (isRequestedByEnclave)
                    {
                        if (string.IsNullOrWhiteSpace(this.Connection.EnclaveAttestationUrl) && Connection.AttestationProtocol != SqlConnectionAttestationProtocol.None)
                        {
                            throw SQL.NoAttestationUrlSpecifiedForEnclaveBasedQuerySpDescribe(this._activeConnection.Parser.EnclaveType);
                        }

                        byte[] keySignature = null;

                        if (!ds.IsDBNull((int)DescribeParameterEncryptionResultSet1.KeySignature))
                        {
                            int keySignatureLength = (int)ds.GetBytes((int)DescribeParameterEncryptionResultSet1.KeySignature, 0, keySignature, 0, 0);
                            keySignature = new byte[keySignatureLength];
                            ds.GetBytes((int)DescribeParameterEncryptionResultSet1.KeySignature, 0, keySignature, 0, keySignatureLength);
                        }

                        SqlSecurityUtility.VerifyColumnMasterKeySignature(providerName, keyPath, isRequestedByEnclave, keySignature, _activeConnection, this);

                        int requestedKey = currentOrdinal;
                        SqlTceCipherInfoEntry cipherInfo;

                        // Lookup the key, failing which throw an exception
                        if (!columnEncryptionKeyTable.TryGetValue(requestedKey, out cipherInfo))
                        {
                            throw SQL.InvalidEncryptionKeyOrdinalEnclaveMetadata(requestedKey, columnEncryptionKeyTable.Count);
                        }

                        if (keysToBeSentToEnclave == null)
                        {
                            keysToBeSentToEnclave = new ConcurrentDictionary<int, SqlTceCipherInfoEntry>();
                            keysToBeSentToEnclave.TryAdd(currentOrdinal, cipherInfo);
                        }
                        else if (!keysToBeSentToEnclave.ContainsKey(currentOrdinal))
                        {
                            keysToBeSentToEnclave.TryAdd(currentOrdinal, cipherInfo);
                        }

                        requiresEnclaveComputations = true;
                    }
                }

                if (!enclaveMetadataExists && !ds.NextResult())
                {
                    throw SQL.UnexpectedDescribeParamFormatParameterMetadata();
                }

                // Find the RPC command that generated this tce request
                if (_batchRPCMode)
                {
                    Debug.Assert(_sqlRPCParameterEncryptionReqArray[resultSetSequenceNumber] != null, "_sqlRPCParameterEncryptionReqArray[resultSetSequenceNumber] should not be null.");

                    // Lookup in the dictionary to get the original rpc request corresponding to the describe parameter encryption request
                    // pointed to by _sqlRPCParameterEncryptionReqArray[resultSetSequenceNumber]
                    rpc = null;
                    lookupDictionaryResult = describeParameterEncryptionRpcOriginalRpcMap.TryGetValue(_sqlRPCParameterEncryptionReqArray[resultSetSequenceNumber++], out rpc);

                    Debug.Assert(lookupDictionaryResult,
                        "Describe Parameter Encryption RPC request key must be present in the dictionary describeParameterEncryptionRpcOriginalRpcMap");
                    Debug.Assert(rpc != null,
                        "Describe Parameter Encryption RPC request's corresponding original rpc request must not be null in the dictionary describeParameterEncryptionRpcOriginalRpcMap");
                }
                else
                {
                    rpc = _rpcArrayOf1[0];
                }

                Debug.Assert(rpc != null, "rpc should not be null here.");

                int userParamCount = rpc.userParams?.Count ?? 0;
                int receivedMetadataCount = 0;
                if (!enclaveMetadataExists || ds.NextResult())
                {
                    // Iterate over the parameter names to read the encryption type info
                    while (ds.Read())
                    {
#if DEBUG
                        rowsAffected++;
#endif
                        Debug.Assert(rpc != null, "Describe Parameter Encryption requested for non-tce spec proc");
                        string parameterName = ds.GetString((int)DescribeParameterEncryptionResultSet2.ParameterName);

                        // When the RPC object gets reused, the parameter array has more parameters that the valid params for the command.
                        // Null is used to indicate the end of the valid part of the array. Refer to GetRPCObject().
                        for (int index = 0; index < userParamCount; index++)
                        {
                            SqlParameter sqlParameter = rpc.userParams[index];
                            Debug.Assert(sqlParameter != null, "sqlParameter should not be null.");

                            if (SqlParameter.ParameterNamesEqual(sqlParameter.ParameterName, parameterName, StringComparison.Ordinal))
                            {
                                Debug.Assert(sqlParameter.CipherMetadata == null, "param.CipherMetadata should be null.");
                                sqlParameter.HasReceivedMetadata = true;
                                receivedMetadataCount += 1;
                                // Found the param, setup the encryption info.
                                byte columnEncryptionType = ds.GetByte((int)DescribeParameterEncryptionResultSet2.ColumnEncryptionType);
                                if ((byte)SqlClientEncryptionType.PlainText != columnEncryptionType)
                                {
                                    byte cipherAlgorithmId = ds.GetByte((int)DescribeParameterEncryptionResultSet2.ColumnEncryptionAlgorithm);
                                    int columnEncryptionKeyOrdinal = ds.GetInt32((int)DescribeParameterEncryptionResultSet2.ColumnEncryptionKeyOrdinal);
                                    byte columnNormalizationRuleVersion = ds.GetByte((int)DescribeParameterEncryptionResultSet2.NormalizationRuleVersion);

                                    // Lookup the key, failing which throw an exception
                                    if (!columnEncryptionKeyTable.TryGetValue(columnEncryptionKeyOrdinal, out cipherInfoEntry))
                                    {
                                        throw SQL.InvalidEncryptionKeyOrdinalParameterMetadata(columnEncryptionKeyOrdinal, columnEncryptionKeyTable.Count);
                                    }

                                    sqlParameter.CipherMetadata = new SqlCipherMetadata(sqlTceCipherInfoEntry: cipherInfoEntry,
                                                                                        ordinal: unchecked((ushort)-1),
                                                                                        cipherAlgorithmId: cipherAlgorithmId,
                                                                                        cipherAlgorithmName: null,
                                                                                        encryptionType: columnEncryptionType,
                                                                                        normalizationRuleVersion: columnNormalizationRuleVersion);

                                    // Decrypt the symmetric key.(This will also validate and throw if needed).
                                    Debug.Assert(_activeConnection != null, @"_activeConnection should not be null");
                                    SqlSecurityUtility.DecryptSymmetricKey(sqlParameter.CipherMetadata, _activeConnection, this);

                                    // This is effective only for BatchRPCMode even though we set it for non-BatchRPCMode also,
                                    // since for non-BatchRPCMode mode, paramoptions gets thrown away and reconstructed in BuildExecuteSql.
                                    int options = (int)(rpc.userParamMap[index] >> 32);
                                    options |= TdsEnums.RPC_PARAM_ENCRYPTED;
                                    rpc.userParamMap[index] = ((((long)options) << 32) | (long)index);
                                }

                                break;
                            }
                        }
                    }
                }

                // When the RPC object gets reused, the parameter array has more parameters that the valid params for the command.
                // Null is used to indicate the end of the valid part of the array. Refer to GetRPCObject().
                if (receivedMetadataCount != userParamCount)
                {
                    for (int index = 0; index < userParamCount; index++)
                    {
                        SqlParameter sqlParameter = rpc.userParams[index];
                        if (!sqlParameter.HasReceivedMetadata && sqlParameter.Direction != ParameterDirection.ReturnValue)
                        {
                            // Encryption MD wasn't sent by the server - we expect the metadata to be sent for all the parameters
                            // that were sent in the original sp_describe_parameter_encryption but not necessarily for return values,
                            // since there might be multiple return values but server will only send for one of them.
                            // For parameters that don't need encryption, the encryption type is set to plaintext.
                            throw SQL.ParamEncryptionMetadataMissing(sqlParameter.ParameterName, rpc.GetCommandTextOrRpcName());
                        }
                    }
                }

#if DEBUG
                Debug.Assert((rowsAffected == 0) || (rowsAffected == RowsAffectedByDescribeParameterEncryption),
                            "number of rows received (if received) for describe parameter encryption should be equal to rows affected by describe parameter encryption.");
#endif


                if (ShouldUseEnclaveBasedWorkflow && (enclaveAttestationParameters != null) && requiresEnclaveComputations)
                {
                    if (!ds.NextResult())
                    {
                        throw SQL.UnexpectedDescribeParamFormatAttestationInfo(this._activeConnection.Parser.EnclaveType);
                    }

                    bool attestationInfoRead = false;

                    while (ds.Read())
                    {
                        if (attestationInfoRead)
                        {
                            throw SQL.MultipleRowsReturnedForAttestationInfo();
                        }

                        int attestationInfoLength = (int)ds.GetBytes((int)DescribeParameterEncryptionResultSet3.AttestationInfo, 0, null, 0, 0);
                        byte[] attestationInfo = new byte[attestationInfoLength];
                        ds.GetBytes((int)DescribeParameterEncryptionResultSet3.AttestationInfo, 0, attestationInfo, 0, attestationInfoLength);

                        SqlConnectionAttestationProtocol attestationProtocol = this._activeConnection.AttestationProtocol;
                        string enclaveType = this._activeConnection.Parser.EnclaveType;

                        EnclaveDelegate.Instance.CreateEnclaveSession(
                            attestationProtocol,
                            enclaveType,
                            GetEnclaveSessionParameters(),
                            attestationInfo,
                            enclaveAttestationParameters,
                            customData,
                            customDataLength,
                            isRetry);
                        enclaveAttestationParameters = null;
                        attestationInfoRead = true;
                    }

                    if (!attestationInfoRead)
                    {
                        throw SQL.AttestationInfoNotReturnedFromSqlServer(this._activeConnection.Parser.EnclaveType, this._activeConnection.EnclaveAttestationUrl);
                    }
                }

                // The server has responded with encryption related information for this rpc request. So clear the needsFetchParameterEncryptionMetadata flag.
                rpc.needsFetchParameterEncryptionMetadata = false;
            } while (ds.NextResult());

            // Verify that we received response for each rpc call needs tce
            if (_batchRPCMode)
            {
                for (int i = 0; i < _RPCList.Count; i++)
                {
                    if (_RPCList[i].needsFetchParameterEncryptionMetadata)
                    {
                        throw SQL.ProcEncryptionMetadataMissing(_RPCList[i].rpcName);
                    }
                }
            }

            // If we are not in Batch RPC mode, update the query cache with the encryption MD.
            if (!_batchRPCMode && ShouldCacheEncryptionMetadata && (_parameters is not null && _parameters.Count > 0))
            {
                SqlQueryMetadataCache.GetInstance().AddQueryMetadata(this, ignoreQueriesWithReturnValueParams: true);
            }
        }

        internal SqlDataReader RunExecuteReader(
            CommandBehavior cmdBehavior,
            RunBehavior runBehavior,
            bool returnStream,
            [CallerMemberName] string method = "")
        {
            Task unused; // sync execution
            SqlDataReader reader = RunExecuteReader(
                cmdBehavior,
                runBehavior,
                returnStream,
                completion: null,
                timeout: CommandTimeout,
                task: out unused,
                usedCache: out _,
                method: method);
            
            Debug.Assert(unused == null, "returned task during synchronous execution");
            return reader;
        }

        // task is created in case of pending asynchronous write, returned SqlDataReader should not be utilized until that task is complete
        internal SqlDataReader RunExecuteReader(
            CommandBehavior cmdBehavior,
            RunBehavior runBehavior,
            bool returnStream,
            TaskCompletionSource<object> completion,
            int timeout,
            out Task task,
            out bool usedCache,
            bool asyncWrite = false,
            bool isRetry = false,
            [CallerMemberName] string method = "")
        {
            bool isAsync = completion != null;
            usedCache = false;

            task = null;

            _rowsAffected = -1;
            _rowsAffectedBySpDescribeParameterEncryption = -1;

            if (0 != (CommandBehavior.SingleRow & cmdBehavior))
            {
                // CommandBehavior.SingleRow implies CommandBehavior.SingleResult
                cmdBehavior |= CommandBehavior.SingleResult;
            }

            // this function may throw for an invalid connection
            // returns false for empty command text
            if (!isRetry)
            {
                ValidateCommand(isAsync, method);
            }

            CheckNotificationStateAndAutoEnlist(); // Only call after validate - requires non null connection!

            TdsParser bestEffortCleanupTarget = null;
            // This section needs to occur AFTER ValidateCommand - otherwise it will AV without a connection.
            RuntimeHelpers.PrepareConstrainedRegions();
            try
            {
                bestEffortCleanupTarget = SqlInternalConnection.GetBestEffortCleanupTarget(_activeConnection);
                SqlStatistics statistics = Statistics;
                if (statistics != null)
                {
                    if ((!this.IsDirty && this.IsPrepared && !_hiddenPrepare)
                        || (this.IsPrepared && _execType == EXECTYPE.PREPAREPENDING))
                    {
                        statistics.SafeIncrement(ref statistics._preparedExecs);
                    }
                    else
                    {
                        statistics.SafeIncrement(ref statistics._unpreparedExecs);
                    }
                }

                // Reset the encryption related state of the command and its parameters.
                ResetEncryptionState();

                if (IsColumnEncryptionEnabled)
                {
                    Task returnTask = null;
                    PrepareForTransparentEncryption(isAsync, timeout, completion, out returnTask, asyncWrite && isAsync, out usedCache, isRetry);
                    Debug.Assert(usedCache || (isAsync == (returnTask != null)), @"if we didn't use the cache, returnTask should be null if and only if async is false.");

                    long firstAttemptStart = ADP.TimerCurrent();

                    try
                    {
                        return RunExecuteReaderTdsWithTransparentParameterEncryption(
                            cmdBehavior,
                            runBehavior,
                            returnStream,
                            isAsync,
                            timeout,
                            out task,
                            asyncWrite && isAsync,
                            isRetry: isRetry,
                            ds: null,
                            describeParameterEncryptionTask: returnTask);
                    }

                    catch (EnclaveDelegate.RetryableEnclaveQueryExecutionException)
                    {
                        if (isRetry)
                        {
                            throw;
                        }

                        // Retry if the command failed with appropriate error.
                        // First invalidate the entry from the cache, so that we refresh our encryption MD.
                        SqlQueryMetadataCache.GetInstance().InvalidateCacheEntry(this);

                        InvalidateEnclaveSession();

                        return RunExecuteReader(
                            cmdBehavior,
                            runBehavior,
                            returnStream,
                            completion,
                            TdsParserStaticMethods.GetRemainingTimeout(timeout, firstAttemptStart),
                            out task,
                            out usedCache,
                            isAsync,
                            isRetry: true,
                            method: method);
                    }

                    catch (SqlException ex)
                    {
                        // We only want to retry once, so don't retry if we are already in retry.
                        // If we didn't use the cache, we don't want to retry.
                        if (isRetry || (!usedCache && !ShouldUseEnclaveBasedWorkflow))
                        {
                            throw;
                        }

                        bool shouldRetry = false;

                        // Check if we have an error indicating that we can retry.
                        for (int i = 0; i < ex.Errors.Count; i++)
                        {

                            if ((usedCache && (ex.Errors[i].Number == TdsEnums.TCE_CONVERSION_ERROR_CLIENT_RETRY)) ||
                                    (ShouldUseEnclaveBasedWorkflow && (ex.Errors[i].Number == TdsEnums.TCE_ENCLAVE_INVALID_SESSION_HANDLE)))
                            {
                                shouldRetry = true;
                                break;
                            }
                        }

                        if (!shouldRetry)
                        {
                            throw;
                        }
                        else
                        {
                            // Retry if the command failed with appropriate error.
                            // First invalidate the entry from the cache, so that we refresh our encryption MD.
                            SqlQueryMetadataCache.GetInstance().InvalidateCacheEntry(this);

                            InvalidateEnclaveSession();

                            return RunExecuteReader(
                                cmdBehavior,
                                runBehavior,
                                returnStream,
                                completion,
                                TdsParserStaticMethods.GetRemainingTimeout(timeout, firstAttemptStart),
                                out task,
                                out usedCache,
                                isAsync,
                                isRetry: true,
                                method: method);
                        }
                    }
                }
                else
                {
                    return RunExecuteReaderTds(cmdBehavior, runBehavior, returnStream, isAsync, timeout, out task, asyncWrite && isAsync, isRetry: isRetry);
                }
            }
            catch (System.OutOfMemoryException e)
            {
                _activeConnection.Abort(e);
                throw;
            }
            catch (System.StackOverflowException e)
            {
                _activeConnection.Abort(e);
                throw;
            }
            catch (System.Threading.ThreadAbortException e)
            {
                _activeConnection.Abort(e);
                SqlInternalConnection.BestEffortCleanup(bestEffortCleanupTarget);
                throw;
            }
        }


        private SqlDataReader RunExecuteReaderTdsWithTransparentParameterEncryption(
            CommandBehavior cmdBehavior,
            RunBehavior runBehavior,
            bool returnStream,
            bool isAsync,
            int timeout,
            out Task task,
            bool asyncWrite,
            bool isRetry,
            SqlDataReader ds = null,
            Task describeParameterEncryptionTask = null)
        {
            Debug.Assert(!asyncWrite || isAsync, "AsyncWrite should be always accompanied by Async");

            if (ds == null && returnStream)
            {
                ds = new SqlDataReader(this, cmdBehavior);
            }

            if (describeParameterEncryptionTask != null)
            {
                long parameterEncryptionStart = ADP.TimerCurrent();
                TaskCompletionSource<object> completion = new TaskCompletionSource<object>();
                AsyncHelper.ContinueTaskWithState(describeParameterEncryptionTask, completion, this,
                    (object state) =>
                    {
                        SqlCommand command = (SqlCommand)state;
                        Task subTask = null;
                        command.GenerateEnclavePackage();
                        command.RunExecuteReaderTds(cmdBehavior, runBehavior, returnStream, isAsync, TdsParserStaticMethods.GetRemainingTimeout(timeout, parameterEncryptionStart), out subTask, asyncWrite, isRetry, ds);
                        if (subTask == null)
                        {
                            completion.SetResult(null);
                        }
                        else
                        {
                            AsyncHelper.ContinueTaskWithState(subTask, completion, completion, static (object state2) => ((TaskCompletionSource<object>)state2).SetResult(null));
                        }
                    },
                    onFailure: static (Exception exception, object state) =>
                    {
                        ((SqlCommand)state).CachedAsyncState?.ResetAsyncState();
                        if (exception != null)
                        {
                            throw exception;
                        }
                    },
                    onCancellation: static (object state) => ((SqlCommand)state).CachedAsyncState?.ResetAsyncState(),
                    connectionToDoom: null,
                    connectionToAbort: _activeConnection);
                task = completion.Task;
                return ds;
            }
            else
            {
                // Synchronous execution.
                GenerateEnclavePackage();
                return RunExecuteReaderTds(cmdBehavior, runBehavior, returnStream, isAsync, timeout, out task, asyncWrite, isRetry, ds);
            }
        }

        private void GenerateEnclavePackage()
        {
            if (keysToBeSentToEnclave == null || keysToBeSentToEnclave.Count <= 0)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(this._activeConnection.EnclaveAttestationUrl) &&
                Connection.AttestationProtocol != SqlConnectionAttestationProtocol.None)
            {
                throw SQL.NoAttestationUrlSpecifiedForEnclaveBasedQueryGeneratingEnclavePackage(this._activeConnection.Parser.EnclaveType);
            }

            string enclaveType = this._activeConnection.Parser.EnclaveType;
            if (string.IsNullOrWhiteSpace(enclaveType))
                throw SQL.EnclaveTypeNullForEnclaveBasedQuery();

            SqlConnectionAttestationProtocol attestationProtocol = this._activeConnection.AttestationProtocol;
            if (attestationProtocol == SqlConnectionAttestationProtocol.NotSpecified)
            {
                throw SQL.AttestationProtocolNotSpecifiedForGeneratingEnclavePackage();
            }

            try
            {
#if DEBUG
                if (_forceRetryableEnclaveQueryExecutionExceptionDuringGenerateEnclavePackage)
                {
                    _forceRetryableEnclaveQueryExecutionExceptionDuringGenerateEnclavePackage = false;
                    throw new EnclaveDelegate.RetryableEnclaveQueryExecutionException("testing", null);
                }
#endif
                this.enclavePackage = EnclaveDelegate.Instance.GenerateEnclavePackage(attestationProtocol, keysToBeSentToEnclave,
                    this.CommandText, enclaveType, GetEnclaveSessionParameters(), _activeConnection, this);
            }
            catch (EnclaveDelegate.RetryableEnclaveQueryExecutionException)
            {
                throw;
            }
            catch (Exception e)
            {
                throw SQL.ExceptionWhenGeneratingEnclavePackage(e);
            }
        }

        private SqlDataReader RunExecuteReaderTds(
            CommandBehavior cmdBehavior,
            RunBehavior runBehavior,
            bool returnStream,
            bool isAsync,
            int timeout,
            out Task task,
            bool asyncWrite,
            bool isRetry,
            SqlDataReader ds = null,
            bool describeParameterEncryptionRequest = false)
        {
            Debug.Assert(!asyncWrite || isAsync, "AsyncWrite should be always accompanied by Async");

            if (ds == null && returnStream)
            {
                ds = new SqlDataReader(this, cmdBehavior);
            }

            Task reconnectTask = _activeConnection.ValidateAndReconnect(null, timeout);

            if (reconnectTask != null)
            {
                long reconnectionStart = ADP.TimerCurrent();
                if (isAsync)
                {
                    TaskCompletionSource<object> completion = new TaskCompletionSource<object>();
                    _activeConnection.RegisterWaitingForReconnect(completion.Task);
                    _reconnectionCompletionSource = completion;
                    CancellationTokenSource timeoutCTS = new CancellationTokenSource();
                    AsyncHelper.SetTimeoutException(completion, timeout, static () => SQL.CR_ReconnectTimeout(), timeoutCTS.Token);
                    AsyncHelper.ContinueTask(reconnectTask, completion,
                        () =>
                        {
                            if (completion.Task.IsCompleted)
                            {
                                return;
                            }
                            Interlocked.CompareExchange(ref _reconnectionCompletionSource, null, completion);
                            timeoutCTS.Cancel();
                            Task subTask;
                            RunExecuteReaderTds(cmdBehavior, runBehavior, returnStream, isAsync, TdsParserStaticMethods.GetRemainingTimeout(timeout, reconnectionStart), out subTask, asyncWrite, isRetry, ds);
                            if (subTask == null)
                            {
                                completion.SetResult(null);
                            }
                            else
                            {
                                AsyncHelper.ContinueTaskWithState(subTask, completion, completion, static (object state) => ((TaskCompletionSource<object>)state).SetResult(null));
                            }
                        },
                        connectionToAbort: _activeConnection
                    );
                    task = completion.Task;
                    return ds;
                }
                else
                {
                    AsyncHelper.WaitForCompletion(reconnectTask, timeout, static () => throw SQL.CR_ReconnectTimeout());
                    timeout = TdsParserStaticMethods.GetRemainingTimeout(timeout, reconnectionStart);
                }
            }

            // make sure we have good parameter information
            // prepare the command
            // execute
            Debug.Assert(_activeConnection.Parser != null, "TdsParser class should not be null in Command.Execute!");

            bool inSchema = (0 != (cmdBehavior & CommandBehavior.SchemaOnly));

            // create a new RPC
            _SqlRPC rpc = null;

            task = null;

            string optionSettings = null;
            bool processFinallyBlock = true;
            bool decrementAsyncCountOnFailure = false;

            if (isAsync)
            {
                _activeConnection.GetOpenTdsConnection().IncrementAsyncCount();
                decrementAsyncCountOnFailure = true;
            }

            try
            {
                if (asyncWrite)
                {
                    _activeConnection.AddWeakReference(this, SqlReferenceCollection.CommandTag);
                }

                GetStateObject();
                Task writeTask = null;

                if (describeParameterEncryptionRequest)
                {
#if DEBUG
                    if (_sleepDuringRunExecuteReaderTdsForSpDescribeParameterEncryption)
                    {
                        Thread.Sleep(10000);
                    }
#endif

                    Debug.Assert(_sqlRPCParameterEncryptionReqArray != null, "RunExecuteReader rpc array not provided for describe parameter encryption request.");
                    writeTask = _stateObj.Parser.TdsExecuteRPC(this, _sqlRPCParameterEncryptionReqArray, timeout, inSchema, this.Notification, _stateObj, CommandType.StoredProcedure == CommandType, sync: !asyncWrite);
                }
                else if (_batchRPCMode)
                {
                    Debug.Assert(inSchema == false, "Batch RPC does not support schema only command behavior");
                    Debug.Assert(!IsPrepared, "Batch RPC should not be prepared!");
                    Debug.Assert(!IsDirty, "Batch RPC should not be marked as dirty!");
                    Debug.Assert(_RPCList != null, "RunExecuteReader rpc array not provided");
                    writeTask = _stateObj.Parser.TdsExecuteRPC(this, _RPCList, timeout, inSchema, this.Notification, _stateObj, CommandType.StoredProcedure == CommandType, sync: !asyncWrite);
                }
                else if ((CommandType.Text == this.CommandType) && (0 == GetParameterCount(_parameters)))
                {
                    // Send over SQL Batch command if we are not a stored proc and have no parameters
                    Debug.Assert(!IsUserPrepared, "CommandType.Text with no params should not be prepared!");

                    if (returnStream)
                    {
                        SqlClientEventSource.Log.TryTraceEvent("SqlCommand.RunExecuteReaderTds | Info | Object Id {0}, Activity Id {1}, Client Connection Id {2}, Command executed as SQLBATCH, Command Text '{3}' ", ObjectID, ActivityCorrelator.Current, Connection?.ClientConnectionId, CommandText);
                    }
                    string text = GetCommandText(cmdBehavior) + GetResetOptionsString(cmdBehavior);

                    //If the query requires enclave computations, pass the enclavepackage in the SQLBatch TDS stream
                    if (requiresEnclaveComputations)
                    {

                        if (this.enclavePackage == null)
                        {
                            throw SQL.NullEnclavePackageForEnclaveBasedQuery(this._activeConnection.Parser.EnclaveType, this._activeConnection.EnclaveAttestationUrl);
                        }

                        writeTask = _stateObj.Parser.TdsExecuteSQLBatch(text, timeout, this.Notification, _stateObj,
                            sync: !asyncWrite, enclavePackage: this.enclavePackage.EnclavePackageBytes);
                    }
                    else
                    {
                        writeTask = _stateObj.Parser.TdsExecuteSQLBatch(text, timeout, this.Notification, _stateObj, sync: !asyncWrite);
                    }
                }
                else if (System.Data.CommandType.Text == this.CommandType)
                {
                    if (this.IsDirty)
                    {
                        Debug.Assert(_cachedMetaData == null || !_dirty, "dirty query should not have cached metadata!"); // can have cached metadata if dirty because of parameters
                        //
                        // someone changed the command text or the parameter schema so we must unprepare the command
                        //
                        // remember that IsDirty includes test for IsPrepared!
                        if (_execType == EXECTYPE.PREPARED)
                        {
                            _hiddenPrepare = true;
                        }
                        Unprepare();
                        IsDirty = false;
                    }

                    if (_execType == EXECTYPE.PREPARED)
                    {
                        Debug.Assert(IsPrepared && _prepareHandle != s_cachedInvalidPrepareHandle, "invalid attempt to call sp_execute without a handle!");
                        rpc = BuildExecute(inSchema);
                    }
                    else if (_execType == EXECTYPE.PREPAREPENDING)
                    {
                        rpc = BuildPrepExec(cmdBehavior);
                        // next time through, only do an exec
                        _execType = EXECTYPE.PREPARED;
                        _preparedConnectionCloseCount = _activeConnection.CloseCount;
                        _preparedConnectionReconnectCount = _activeConnection.ReconnectCount;
                        // mark ourselves as preparing the command
                        _inPrepare = true;
                    }
                    else
                    {
                        Debug.Assert(_execType == EXECTYPE.UNPREPARED, "Invalid execType!");
                        BuildExecuteSql(cmdBehavior, null, _parameters, ref rpc);
                    }

                    // if 2000, then set NOMETADATA_UNLESSCHANGED flag
                    rpc.options = TdsEnums.RPC_NOMETADATA;
                    if (returnStream)
                    {
                        SqlClientEventSource.Log.TryTraceEvent("SqlCommand.RunExecuteReaderTds | Info | Object Id {0}, Activity Id {1}, Client Connection Id {2}, Command executed as RPC, RPC Name '{3}' ", ObjectID, ActivityCorrelator.Current, Connection?.ClientConnectionId, rpc?.rpcName);
                    }

                    // TODO: Medusa: Unprepare only happens for SQL 7.0 which may be broken anyway (it's not re-prepared). Consider removing the reset here if we're really dropping 7.0 support.
                    Debug.Assert(_rpcArrayOf1[0] == rpc);
                    writeTask = _stateObj.Parser.TdsExecuteRPC(this, _rpcArrayOf1, timeout, inSchema, this.Notification, _stateObj, CommandType.StoredProcedure == CommandType, sync: !asyncWrite);
                }
                else
                {
                    Debug.Assert(this.CommandType == System.Data.CommandType.StoredProcedure, "unknown command type!");

                    BuildRPC(inSchema, _parameters, ref rpc);

                    // if we need to augment the command because a user has changed the command behavior (e.g. FillSchema)
                    // then batch sql them over.  This is inefficient (3 round trips) but the only way we can get metadata only from
                    // a stored proc
                    optionSettings = GetSetOptionsString(cmdBehavior);
                    if (returnStream)
                    {
                        SqlClientEventSource.Log.TryTraceEvent("SqlCommand.RunExecuteReaderTds | Info | Object Id {0}, Activity Id {1}, Client Connection Id {2}, Command executed as RPC, RPC Name '{3}' ", ObjectID, ActivityCorrelator.Current, Connection?.ClientConnectionId, rpc?.rpcName);
                    }

                    // turn set options ON
                    if (optionSettings != null)
                    {
                        Task executeTask = _stateObj.Parser.TdsExecuteSQLBatch(optionSettings, timeout, this.Notification, _stateObj, sync: true);
                        Debug.Assert(executeTask == null, "Shouldn't get a task when doing sync writes");
                        Debug.Assert(_stateObj._syncOverAsync, "Should not attempt pends in a synchronous call");
                        TdsOperationStatus result = _stateObj.Parser.TryRun(RunBehavior.UntilDone, this, null, null, _stateObj, out _);
                        if (result != TdsOperationStatus.Done)
                        {
                            throw SQL.SynchronousCallMayNotPend();
                        }
                        // and turn OFF when the ds exhausts the stream on Close()
                        optionSettings = GetResetOptionsString(cmdBehavior);
                    }

                    // execute sp
                    Debug.Assert(_rpcArrayOf1[0] == rpc);
                    writeTask = _stateObj.Parser.TdsExecuteRPC(this, _rpcArrayOf1, timeout, inSchema, this.Notification, _stateObj, CommandType.StoredProcedure == CommandType, sync: !asyncWrite);
                }

                Debug.Assert(writeTask == null || isAsync, "Returned task in sync mode");

                if (isAsync)
                {
                    decrementAsyncCountOnFailure = false;
                    if (writeTask != null)
                    {
                        task = AsyncHelper.CreateContinuationTask(writeTask, () =>
                        {
                            _activeConnection.GetOpenTdsConnection(); // it will throw if connection is closed
                            CachedAsyncState.SetAsyncReaderState(ds, runBehavior, optionSettings);
                        },
                                 onFailure: (exc) =>
                                 {
                                     _activeConnection.GetOpenTdsConnection().DecrementAsyncCount();
                                 });
                    }
                    else
                    {
                        CachedAsyncState.SetAsyncReaderState(ds, runBehavior, optionSettings);
                    }
                }
                else
                {
                    // Always execute - even if no reader!
                    FinishExecuteReader(ds, runBehavior, optionSettings, isInternal: false, forDescribeParameterEncryption: false, shouldCacheForAlwaysEncrypted: !describeParameterEncryptionRequest);
                }
            }
            catch (Exception e)
            {
                processFinallyBlock = ADP.IsCatchableExceptionType(e);
                if (decrementAsyncCountOnFailure)
                {
                    SqlInternalConnectionTds innerConnectionTds = (_activeConnection.InnerConnection as SqlInternalConnectionTds);
                    if (innerConnectionTds != null)
                    {
                        // it may be closed
                        innerConnectionTds.DecrementAsyncCount();
                    }
                }
                throw;
            }
            finally
            {
                if (processFinallyBlock && !isAsync)
                {
                    // When executing async, we need to keep the _stateObj alive...
                    PutStateObject();
                }
            }

            Debug.Assert(isAsync || _stateObj == null, "non-null state object in RunExecuteReader");
            return ds;
        }

        private SqlDataReader CompleteAsyncExecuteReader(bool isInternal = false, bool forDescribeParameterEncryption = false)
        {
            SqlDataReader ds = CachedAsyncState.CachedAsyncReader; // should not be null
            bool processFinallyBlock = true;
            try
            {
                FinishExecuteReader(ds, CachedAsyncState.CachedRunBehavior, CachedAsyncState.CachedSetOptions, isInternal, forDescribeParameterEncryption, shouldCacheForAlwaysEncrypted: !forDescribeParameterEncryption);
            }
            catch (Exception e)
            {
                processFinallyBlock = ADP.IsCatchableExceptionType(e);
                throw;
            }
            finally
            {
                if (processFinallyBlock)
                {
                    // Don't reset the state for internal End. The user End will do that eventually.
                    if (!isInternal)
                    {
                        CachedAsyncState.ResetAsyncState();
                    }
                    PutStateObject();
                }
            }

            return ds;
        }

        private void FinishExecuteReader(SqlDataReader ds, RunBehavior runBehavior, string resetOptionsString, bool isInternal, bool forDescribeParameterEncryption, bool shouldCacheForAlwaysEncrypted = true)
        {
            // always wrap with a try { FinishExecuteReader(...) } finally { PutStateObject(); }

            // If this is not for internal usage, notify the dependency. If we have already initiated the end internally, the reader should be ready, so just return.
            if (!isInternal && !forDescribeParameterEncryption)
            {
                NotifyDependency();

                if (_internalEndExecuteInitiated)
                {
                    Debug.Assert(_stateObj == null);
                    return;
                }
            }

            if (runBehavior == RunBehavior.UntilDone)
            {
                try
                {
                    Debug.Assert(_stateObj._syncOverAsync, "Should not attempt pends in a synchronous call");
                    TdsOperationStatus result = _stateObj.Parser.TryRun(RunBehavior.UntilDone, this, ds, null, _stateObj, out _);
                    if (result != TdsOperationStatus.Done)
                    {
                        throw SQL.SynchronousCallMayNotPend();
                    }
                }
                catch (Exception e)
                {
                    if (ADP.IsCatchableExceptionType(e))
                    {
                        if (_inPrepare)
                        {
                            // The flag is expected to be reset by OnReturnValue.  We should receive
                            // the handle unless command execution failed.  If fail, move back to pending
                            // state.
                            _inPrepare = false;                  // reset the flag
                            IsDirty = true;                      // mark command as dirty so it will be prepared next time we're coming through
                            _execType = EXECTYPE.PREPAREPENDING; // reset execution type to pending
                        }

                        if (ds != null)
                        {
                            ds.Close();
                        }
                    }
                    throw;
                }
            }

            // bind the parser to the reader if we get this far
            if (ds != null)
            {
                ds.Bind(_stateObj);
                _stateObj = null;   // the reader now owns this...
                ds.ResetOptionsString = resetOptionsString;

                // bind this reader to this connection now
                _activeConnection.AddWeakReference(ds, SqlReferenceCollection.DataReaderTag);

                // force this command to start reading data off the wire.
                // this will cause an error to be reported at Execute() time instead of Read() time
                // if the command is not set.
                try
                {
                    //This flag indicates if the datareader's metadata should be cached in this SqlCommand.
                    //Metadata associated with sp_describe_parameter_metadats's datareader should not be cached.
                    //Ideally, we should be using "forDescribeParameterEncryption" flag for this, but this flag's
                    //semantics are overloaded with async workflow and this flag is always false for sync workflow.
                    //Since we are very close to a release and changing the semantics for "forDescribeParameterEncryption"
                    //is risky, we introduced a new parameter to determine whether we should cache a datareader's metadata or not.
                    if (shouldCacheForAlwaysEncrypted)
                    {
                        _cachedMetaData = ds.MetaData;
                    }
                    else
                    {
                        //we need this call to ensure that the datareader is properly intialized, the getter is initializing state in SqlDataReader
                        _SqlMetaDataSet temp = ds.MetaData;
                    }
                    ds.IsInitialized = true;
                }
                catch (Exception e)
                {
                    if (ADP.IsCatchableExceptionType(e))
                    {
                        if (_inPrepare)
                        {
                            // The flag is expected to be reset by OnReturnValue.  We should receive
                            // the handle unless command execution failed.  If fail, move back to pending
                            // state.
                            _inPrepare = false;                  // reset the flag
                            IsDirty = true;                      // mark command as dirty so it will be prepared next time we're coming through
                            _execType = EXECTYPE.PREPAREPENDING; // reset execution type to pending
                        }

                        ds.Close();
                    }

                    throw;
                }
            }
        }

        /// <include file='../../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlCommand.xml' path='docs/members[@name="SqlCommand"]/Clone/*'/>
        public SqlCommand Clone()
        {
            SqlCommand clone = new SqlCommand(this);
            SqlClientEventSource.Log.TryTraceEvent("SqlCommand.Clone | API | Object Id {0}, Clone Object Id {1}, Client Connection Id {2}", ObjectID, clone.ObjectID, Connection?.ClientConnectionId);
            return clone;
        }

        object ICloneable.Clone() =>
            Clone();

        private Task<T> RegisterForConnectionCloseNotification<T>(Task<T> outterTask)
        {
            SqlConnection connection = _activeConnection;
            if (connection == null)
            {
                // No connection
                throw ADP.ClosedConnectionError();
            }

            return connection.RegisterForConnectionCloseNotification(outterTask, this, SqlReferenceCollection.CommandTag);
        }

        // validates that a command has commandText and a non-busy open connection
        // throws exception for error case, returns false if the commandText is empty
        private void ValidateCommand(bool isAsync, [CallerMemberName] string method = "")
        {
            if (_activeConnection == null)
            {
                throw ADP.ConnectionRequired(method);
            }

            // Ensure that the connection is open and that the Parser is in the correct state
            SqlInternalConnectionTds tdsConnection = _activeConnection.InnerConnection as SqlInternalConnectionTds;

            // Ensure that if column encryption override was used then server supports its
            if (((SqlCommandColumnEncryptionSetting.UseConnectionSetting == ColumnEncryptionSetting && _activeConnection.IsColumnEncryptionSettingEnabled)
                 || (ColumnEncryptionSetting == SqlCommandColumnEncryptionSetting.Enabled || ColumnEncryptionSetting == SqlCommandColumnEncryptionSetting.ResultSetOnly))
                && tdsConnection != null
                && tdsConnection.Parser != null
                && !tdsConnection.Parser.IsColumnEncryptionSupported)
            {
                throw SQL.TceNotSupported();
            }

            if (tdsConnection != null)
            {
                var parser = tdsConnection.Parser;
                if ((parser == null) || (parser.State == TdsParserState.Closed))
                {
                    throw ADP.OpenConnectionRequired(method, ConnectionState.Closed);
                }
                else if (parser.State != TdsParserState.OpenLoggedIn)
                {
                    throw ADP.OpenConnectionRequired(method, ConnectionState.Broken);
                }
            }
            else if (_activeConnection.State == ConnectionState.Closed)
            {
                throw ADP.OpenConnectionRequired(method, ConnectionState.Closed);
            }
            else if (_activeConnection.State == ConnectionState.Broken)
            {
                throw ADP.OpenConnectionRequired(method, ConnectionState.Broken);
            }

            ValidateAsyncCommand();

            TdsParser bestEffortCleanupTarget = null;
            RuntimeHelpers.PrepareConstrainedRegions();
            try
            {
                bestEffortCleanupTarget = SqlInternalConnection.GetBestEffortCleanupTarget(_activeConnection);
                // close any non MARS dead readers, if applicable, and then throw if still busy.
                // Throw if we have a live reader on this command
                _activeConnection.ValidateConnectionForExecute(method, this);
            }
            catch (System.OutOfMemoryException e)
            {
                _activeConnection.Abort(e);
                throw;
            }
            catch (System.StackOverflowException e)
            {
                _activeConnection.Abort(e);
                throw;
            }
            catch (System.Threading.ThreadAbortException e)
            {
                _activeConnection.Abort(e);
                SqlInternalConnection.BestEffortCleanup(bestEffortCleanupTarget);
                throw;
            }
            // Check to see if the currently set transaction has completed.  If so,
            // null out our local reference.
            if (_transaction != null && _transaction.Connection == null)
            {
                _transaction = null;
            }

            // throw if the connection is in a transaction but there is no
            // locally assigned transaction object
            if (_activeConnection.HasLocalTransactionFromAPI && _transaction == null)
            {
                throw ADP.TransactionRequired(method);
            }

            // if we have a transaction, check to ensure that the active
            // connection property matches the connection associated with
            // the transaction
            if (_transaction != null && _activeConnection != _transaction.Connection)
            {
                throw ADP.TransactionConnectionMismatch();
            }

            if (string.IsNullOrEmpty(this.CommandText))
            {
                throw ADP.CommandTextRequired(method);
            }
        }

        private void ValidateAsyncCommand()
        {
            if (CachedAsyncState.PendingAsyncOperation)
            {
                // Enforce only one pending async execute at a time.
                if (CachedAsyncState.IsActiveConnectionValid(_activeConnection))
                {
                    throw SQL.PendingBeginXXXExists();
                }
                else
                {
                    _stateObj = null; // Session was re-claimed by session pool upon connection close.
                    CachedAsyncState.ResetAsyncState();
                }
            }
        }

        private void GetStateObject(TdsParser parser = null)
        {
            Debug.Assert(_stateObj == null, "StateObject not null on GetStateObject");
            Debug.Assert(_activeConnection != null, "no active connection?");

            if (_pendingCancel)
            {
                _pendingCancel = false; // Not really needed, but we'll reset anyways.

                // If a pendingCancel exists on the object, we must have had a Cancel() call
                // between the point that we entered an Execute* API and the point in Execute* that
                // we proceeded to call this function and obtain a stateObject.  In that case,
                // we now throw a cancelled error.
                throw SQL.OperationCancelled();
            }

            if (parser == null)
            {
                parser = _activeConnection.Parser;
                if ((parser == null) || (parser.State == TdsParserState.Broken) || (parser.State == TdsParserState.Closed))
                {
                    // Connection's parser is null as well, therefore we must be closed
                    throw ADP.ClosedConnectionError();
                }
            }

            TdsParserStateObject stateObj = parser.GetSession(this);
            stateObj.StartSession(this);

            _stateObj = stateObj;

            if (_pendingCancel)
            {
                _pendingCancel = false; // Not really needed, but we'll reset anyways.

                // If a pendingCancel exists on the object, we must have had a Cancel() call
                // between the point that we entered this function and the point where we obtained
                // and actually assigned the stateObject to the local member.  It is possible
                // that the flag is set as well as a call to stateObj.Cancel - though that would
                // be a no-op.  So - throw.
                throw SQL.OperationCancelled();
            }
        }

        private void ReliablePutStateObject()
        {
            TdsParser bestEffortCleanupTarget = null;
            RuntimeHelpers.PrepareConstrainedRegions();
            try
            {
                bestEffortCleanupTarget = SqlInternalConnection.GetBestEffortCleanupTarget(_activeConnection);
                PutStateObject();
            }
            catch (System.OutOfMemoryException e)
            {
                _activeConnection.Abort(e);
                throw;
            }
            catch (System.StackOverflowException e)
            {
                _activeConnection.Abort(e);
                throw;
            }
            catch (System.Threading.ThreadAbortException e)
            {
                _activeConnection.Abort(e);
                SqlInternalConnection.BestEffortCleanup(bestEffortCleanupTarget);
                throw;
            }
        }

        private void PutStateObject()
        {
            TdsParserStateObject stateObj = _stateObj;
            _stateObj = null;

            if (stateObj != null)
            {
                stateObj.CloseSession();
            }
        }

        internal void OnDoneDescribeParameterEncryptionProc(TdsParserStateObject stateObj)
        {
            // called per rpc batch complete
            if (_batchRPCMode)
            {
                OnDone(stateObj, _currentlyExecutingDescribeParameterEncryptionRPC, _sqlRPCParameterEncryptionReqArray, _rowsAffected);
                _currentlyExecutingDescribeParameterEncryptionRPC++;
            }
        }

        internal void OnDoneProc(TdsParserStateObject stateObject)
        {
            // called per rpc batch complete
            if (_batchRPCMode)
            {
                OnDone(stateObject, _currentlyExecutingBatch, _RPCList, _rowsAffected);
                _currentlyExecutingBatch++;
                Debug.Assert(_RPCList.Count >= _currentlyExecutingBatch, "OnDoneProc: Too many DONEPROC events");
            }
        }

        private static void OnDone(TdsParserStateObject stateObj, int index, IList<_SqlRPC> rpcList, int rowsAffected)
        {
            _SqlRPC current = rpcList[index];
            _SqlRPC previous = (index > 0) ? rpcList[index - 1] : null;

            // track the records affected for the just completed rpc batch
            // _rowsAffected is cumulative for ExecuteNonQuery across all rpc batches
            current.cumulativeRecordsAffected = rowsAffected;

            current.recordsAffected =
                (((previous != null) && (0 <= rowsAffected))
                    ? (rowsAffected - Math.Max(previous.cumulativeRecordsAffected, 0))
                    : rowsAffected);

            if (current.batchCommand != null)
            {
                current.batchCommand.SetRecordAffected(current.recordsAffected.GetValueOrDefault());
            }

            // track the error collection (not available from TdsParser after ExecuteNonQuery)
            // and the which errors are associated with the just completed rpc batch
            current.errorsIndexStart = previous?.errorsIndexEnd ?? 0;
            current.errorsIndexEnd = stateObj.ErrorCount;
            current.errors = stateObj._errors;

            // track the warning collection (not available from TdsParser after ExecuteNonQuery)
            // and the which warnings are associated with the just completed rpc batch
            current.warningsIndexStart = previous?.warningsIndexEnd ?? 0;
            current.warningsIndexEnd = stateObj.WarningCount;
            current.warnings = stateObj._warnings;
        }

        internal void OnReturnStatus(int status)
        {
            if (_inPrepare)
                return;

            // Don't set the return status if this is the status for sp_describe_parameter_encryption.
            if (IsDescribeParameterEncryptionRPCCurrentlyInProgress)
                return;

            SqlParameterCollection parameters = _parameters;
            if (_batchRPCMode)
            {
                if (_RPCList.Count > _currentlyExecutingBatch)
                {
                    parameters = _RPCList[_currentlyExecutingBatch].userParams;
                }
                else
                {
                    Debug.Fail("OnReturnStatus: SqlCommand got too many DONEPROC events");
                    parameters = null;
                }
            }
            // see if a return value is bound
            int count = GetParameterCount(parameters);
            for (int i = 0; i < count; i++)
            {
                SqlParameter parameter = parameters[i];
                if (parameter.Direction == ParameterDirection.ReturnValue)
                {
                    object v = parameter.Value;

                    // if the user bound a sqlint32 (the only valid one for status, use it)
                    if (v != null && (v.GetType() == typeof(SqlInt32)))
                    {
                        parameter.Value = new SqlInt32(status); // value type
                    }
                    else
                    {
                        parameter.Value = status;
                    }

                    // If we are not in Batch RPC mode, update the query cache with the encryption MD.
                    // We can do this now that we have distinguished between ReturnValue and ReturnStatus.
                    // Read comment in AddQueryMetadata() for more details.
                    if (!_batchRPCMode && CachingQueryMetadataPostponed &&
                        ShouldCacheEncryptionMetadata && (_parameters is not null && _parameters.Count > 0))
                    {
                        SqlQueryMetadataCache.GetInstance().AddQueryMetadata(this, ignoreQueriesWithReturnValueParams: false);
                    }

                    break;
                }
            }
        }

        //
        // Move the return value to the corresponding output parameter.
        // Return parameters are sent in the order in which they were defined in the procedure.
        // If named, match the parameter name, otherwise fill in based on ordinal position.
        // If the parameter is not bound, then ignore the return value.
        //
        internal void OnReturnValue(SqlReturnValue rec, TdsParserStateObject stateObj)
        {
            if (_inPrepare)
            {
                if (!rec.value.IsNull)
                {
                    _prepareHandle = rec.value.Int32;
                }
                _inPrepare = false;
                return;
            }

            SqlParameterCollection parameters = GetCurrentParameterCollection();
            int count = GetParameterCount(parameters);

            SqlParameter thisParam = GetParameterForOutputValueExtraction(parameters, rec.parameter, count);

            if (thisParam != null)
            {
                // If the parameter's direction is InputOutput, Output or ReturnValue and it needs to be transparently encrypted/decrypted
                // then simply decrypt, deserialize and set the value.
                if (rec.cipherMD != null &&
                    thisParam.CipherMetadata != null &&
                    (thisParam.Direction == ParameterDirection.Output ||
                    thisParam.Direction == ParameterDirection.InputOutput ||
                    thisParam.Direction == ParameterDirection.ReturnValue))
                {
                    if (rec.tdsType != TdsEnums.SQLBIGVARBINARY)
                    {
                        throw SQL.InvalidDataTypeForEncryptedParameter(thisParam.GetPrefixedParameterName(), rec.tdsType, TdsEnums.SQLBIGVARBINARY);
                    }

                    // Decrypt the ciphertext
                    TdsParser parser = _activeConnection.Parser;
                    if ((parser == null) || (parser.State == TdsParserState.Closed) || (parser.State == TdsParserState.Broken))
                    {
                        throw ADP.ClosedConnectionError();
                    }

                    if (!rec.value.IsNull)
                    {
                        try
                        {
                            Debug.Assert(_activeConnection != null, @"_activeConnection should not be null");

                            // Get the key information from the parameter and decrypt the value.
                            rec.cipherMD.EncryptionInfo = thisParam.CipherMetadata.EncryptionInfo;
                            byte[] unencryptedBytes = SqlSecurityUtility.DecryptWithKey(rec.value.ByteArray, rec.cipherMD, _activeConnection, this);

                            if (unencryptedBytes != null)
                            {
                                // Denormalize the value and convert it to the parameter type.
                                SqlBuffer buffer = new SqlBuffer();
                                parser.DeserializeUnencryptedValue(buffer, unencryptedBytes, rec, stateObj, rec.NormalizationRuleVersion);
                                thisParam.SetSqlBuffer(buffer);
                            }
                        }
                        catch (Exception e)
                        {
                            throw SQL.ParamDecryptionFailed(thisParam.GetPrefixedParameterName(), null, e);
                        }
                    }
                    else
                    {
                        // Create a new SqlBuffer and set it to null
                        // Note: We can't reuse the SqlBuffer in "rec" below since it's already been set (to varbinary)
                        // in previous call to TryProcessReturnValue().
                        // Note 2: We will be coming down this code path only if the Command Setting is set to use TCE.
                        // We pass the command setting as TCE enabled in the below call for this reason.
                        SqlBuffer buff = new SqlBuffer();
                        TdsParser.GetNullSqlValue(buff, rec, SqlCommandColumnEncryptionSetting.Enabled, parser.Connection);
                        thisParam.SetSqlBuffer(buff);
                    }
                }
                else
                {
                    // copy over data

                    // if the value user has supplied a SqlType class, then just copy over the SqlType, otherwise convert
                    // to the com type
                    object val = thisParam.Value;

                    //set the UDT value as typed object rather than bytes
                    if (SqlDbType.Udt == thisParam.SqlDbType)
                    {
                        object data = null;
                        try
                        {
                            Connection.CheckGetExtendedUDTInfo(rec, true);

                            //extract the byte array from the param value
                            if (rec.value.IsNull)
                            {
                                data = DBNull.Value;
                            }
                            else
                            {
                                data = rec.value.ByteArray; //should work for both sql and non-sql values
                            }

                            //call the connection to instantiate the UDT object
                            thisParam.Value = Connection.GetUdtValue(data, rec, false);
                        }
                        catch (FileNotFoundException e)
                        {
                            // Assign Assembly.Load failure in case where assembly not on client.
                            // This allows execution to complete and failure on SqlParameter.Value.
                            thisParam.SetUdtLoadError(e);
                        }
                        catch (FileLoadException e)
                        {
                            // Assign Assembly.Load failure in case where assembly cannot be loaded on client.
                            // This allows execution to complete and failure on SqlParameter.Value.
                            thisParam.SetUdtLoadError(e);
                        }

                        return;
                    }
                    else
                    {
                        thisParam.SetSqlBuffer(rec.value);
                    }

                    MetaType mt = MetaType.GetMetaTypeFromSqlDbType(rec.type, rec.IsMultiValued);

                    if (rec.type == SqlDbType.Decimal)
                    {
                        thisParam.ScaleInternal = rec.scale;
                        thisParam.PrecisionInternal = rec.precision;
                    }
                    else if (mt.IsVarTime)
                    {
                        thisParam.ScaleInternal = rec.scale;
                    }
                    else if (rec.type == SqlDbType.Xml)
                    {
                        SqlCachedBuffer cachedBuffer = (thisParam.Value as SqlCachedBuffer);
                        if (cachedBuffer != null)
                        {
                            thisParam.Value = cachedBuffer.ToString();
                        }
                    }

                    if (rec.collation != null)
                    {
                        Debug.Assert(mt.IsCharType, "Invalid collation structure for non-char type");
                        thisParam.Collation = rec.collation;
                    }
                }
            }

            return;
        }

        private SqlParameterCollection GetCurrentParameterCollection()
        {
            if (_batchRPCMode)
            {
                if (_RPCList.Count > _currentlyExecutingBatch)
                {
                    return _RPCList[_currentlyExecutingBatch].userParams;
                }
                else
                {
                    Debug.Fail("OnReturnValue: SqlCommand got too many DONEPROC events");
                    return null;
                }
            }
            else
            {
                return _parameters;
            }
        }

        private SqlParameter GetParameterForOutputValueExtraction(SqlParameterCollection parameters,
                        string paramName, int paramCount)
        {
            SqlParameter thisParam = null;
            bool foundParam = false;

            if (paramName == null)
            {
                // rec.parameter should only be null for a return value from a function
                for (int i = 0; i < paramCount; i++)
                {
                    thisParam = parameters[i];
                    // searching for ReturnValue
                    if (thisParam.Direction == ParameterDirection.ReturnValue)
                    {
                        foundParam = true;
                        break; // found it
                    }
                }
            }
            else
            {
                for (int i = 0; i < paramCount; i++)
                {
                    thisParam = parameters[i];
                    // searching for Output or InputOutput or ReturnValue with matching name
                    if (
                        thisParam.Direction != ParameterDirection.Input &&
                        thisParam.Direction != ParameterDirection.ReturnValue &&
                        SqlParameter.ParameterNamesEqual(paramName, thisParam.ParameterName, StringComparison.Ordinal)
                    )
                    {
                        foundParam = true;
                        break; // found it
                    }
                }
            }
            if (foundParam)
                return thisParam;
            else
                return null;
        }

        private void GetRPCObject(int systemParamCount, int userParamCount, ref _SqlRPC rpc, bool forSpDescribeParameterEncryption = false)
        {
            // Designed to minimize necessary allocations
            if (rpc == null)
            {
                if (!forSpDescribeParameterEncryption)
                {
                    if (_rpcArrayOf1 == null)
                    {
                        _rpcArrayOf1 = new _SqlRPC[1];
                        _rpcArrayOf1[0] = new _SqlRPC();
                    }

                    rpc = _rpcArrayOf1[0];
                }
                else
                {
                    if (_rpcForEncryption == null)
                    {
                        _rpcForEncryption = new _SqlRPC();
                    }

                    rpc = _rpcForEncryption;
                }
            }

            rpc.ProcID = 0;
            rpc.rpcName = null;
            rpc.options = 0;
            rpc.systemParamCount = systemParamCount;

            rpc.recordsAffected = default(int?);
            rpc.cumulativeRecordsAffected = -1;

            rpc.errorsIndexStart = 0;
            rpc.errorsIndexEnd = 0;
            rpc.errors = null;

            rpc.warningsIndexStart = 0;
            rpc.warningsIndexEnd = 0;
            rpc.warnings = null;
            rpc.needsFetchParameterEncryptionMetadata = false;

            int currentCount = rpc.systemParams?.Length ?? 0;

            // Make sure there is enough space in the parameters and paramoptions arrays
            if (currentCount < systemParamCount)
            {
                Array.Resize(ref rpc.systemParams, systemParamCount);
                Array.Resize(ref rpc.systemParamOptions, systemParamCount);
                for (int index = currentCount; index < systemParamCount; index++)
                {
                    rpc.systemParams[index] = new SqlParameter();
                }
            }

            for (int ii = 0; ii < systemParamCount; ii++)
            {
                rpc.systemParamOptions[ii] = 0;
            }

            if ((rpc.userParamMap?.Length ?? 0) < userParamCount)
            {
                Array.Resize(ref rpc.userParamMap, userParamCount);
            }
        }

        private void SetUpRPCParameters(_SqlRPC rpc, bool inSchema, SqlParameterCollection parameters)
        {
            int paramCount = GetParameterCount(parameters);
            int userParamCount = 0;

            for (int index = 0; index < paramCount; index++)
            {
                SqlParameter parameter = parameters[index];
                parameter.Validate(index, CommandType.StoredProcedure == CommandType);

                // func will change type to that with a 4 byte length if the type has a two
                // byte length and a parameter length > than that expressible in 2 bytes
                if ((!parameter.ValidateTypeLengths().IsPlp) && (parameter.Direction != ParameterDirection.Output))
                {
                    parameter.FixStreamDataForNonPLP();
                }

                if (ShouldSendParameter(parameter))
                {
                    byte options = 0;

                    // set output bit
                    if (parameter.Direction == ParameterDirection.InputOutput || parameter.Direction == ParameterDirection.Output)
                    {
                        options = TdsEnums.RPC_PARAM_BYREF;
                    }

                    // Set the encryped bit, if the parameter is to be encrypted.
                    if (parameter.CipherMetadata != null)
                    {
                        options |= TdsEnums.RPC_PARAM_ENCRYPTED;
                    }

                    // set default value bit
                    if (parameter.Direction != ParameterDirection.Output)
                    {
                        // remember that Convert.IsEmpty is null, DBNull.Value is a database null!

                        // Don't assume a default value exists for parameters in the case when
                        // the user is simply requesting schema.
                        // TVPs use DEFAULT and do not allow NULL, even for schema only.
                        if (parameter.Value == null && (!inSchema || SqlDbType.Structured == parameter.SqlDbType))
                        {
                            options |= TdsEnums.RPC_PARAM_DEFAULT;
                        }

                        // detect incorrectly derived type names unchanged by the caller and fix them
                        if (parameter.IsDerivedParameterTypeName)
                        {
                            string[] parts = MultipartIdentifier.ParseMultipartIdentifier(parameter.TypeName, "[\"", "]\"", Strings.SQL_TDSParserTableName, false);
                            if (parts != null && parts.Length == 4) // will always return int[4] right justified
                            {
                                if (
                                    parts[3] != null && // name must not be null
                                    parts[2] != null && // schema must not be null
                                    parts[1] != null // server should not be null or we don't need to remove it
                                )
                                {
                                    parameter.TypeName = QuoteIdentifier(parts, 2, 2);
                                }
                            }
                        }
                    }

                    rpc.userParamMap[userParamCount] = ((((long)options) << 32) | (long)index);
                    userParamCount += 1;

                    // Must set parameter option bit for LOB_COOKIE if unfilled LazyMat blob
                }
            }

            rpc.userParamCount = userParamCount;
            rpc.userParams = parameters;
        }

        private _SqlRPC BuildPrepExec(CommandBehavior behavior)
        {
            Debug.Assert(System.Data.CommandType.Text == this.CommandType, "invalid use of sp_prepexec for stored proc invocation!");
            SqlParameter sqlParam;

            const int systemParameterCount = 3;
            int userParameterCount = CountSendableParameters(_parameters);

            _SqlRPC rpc = null;
            GetRPCObject(systemParameterCount, userParameterCount, ref rpc);

            rpc.ProcID = TdsEnums.RPC_PROCID_PREPEXEC;
            rpc.rpcName = TdsEnums.SP_PREPEXEC;

            //@handle
            sqlParam = rpc.systemParams[0];
            sqlParam.SqlDbType = SqlDbType.Int;
            sqlParam.Value = _prepareHandle;
            sqlParam.Size = 4;
            sqlParam.Direction = ParameterDirection.InputOutput;
            rpc.systemParamOptions[0] = TdsEnums.RPC_PARAM_BYREF;

            //@batch_params
            string paramList = BuildParamList(_stateObj.Parser, _parameters);
            sqlParam = rpc.systemParams[1];
            sqlParam.SqlDbType = ((paramList.Length << 1) <= TdsEnums.TYPE_SIZE_LIMIT) ? SqlDbType.NVarChar : SqlDbType.NText;
            sqlParam.Value = paramList;
            sqlParam.Size = paramList.Length;
            sqlParam.Direction = ParameterDirection.Input;

            //@batch_text
            string text = GetCommandText(behavior);
            sqlParam = rpc.systemParams[2];
            sqlParam.SqlDbType = ((text.Length << 1) <= TdsEnums.TYPE_SIZE_LIMIT) ? SqlDbType.NVarChar : SqlDbType.NText;
            sqlParam.Size = text.Length;
            sqlParam.Value = text;
            sqlParam.Direction = ParameterDirection.Input;

            SetUpRPCParameters(rpc, false, _parameters);
            return rpc;
        }

        //
        // returns true if the parameter is not a return value
        // and it's value is not DBNull (for a nullable parameter)
        //
        private static bool ShouldSendParameter(SqlParameter p, bool includeReturnValue = false)
        {
            switch (p.Direction)
            {
                case ParameterDirection.ReturnValue:
                    // return value parameters are not sent, except for the parameter list of sp_describe_parameter_encryption
                    return includeReturnValue;
                case ParameterDirection.Output:
                case ParameterDirection.InputOutput:
                case ParameterDirection.Input:
                    // InputOutput/Output parameters are aways sent
                    return true;
                default:
                    Debug.Fail("Invalid ParameterDirection!");
                    return false;
            }
        }

        private static int CountSendableParameters(SqlParameterCollection parameters)
        {
            int cParams = 0;

            if (parameters != null)
            {
                int count = parameters.Count;
                for (int i = 0; i < count; i++)
                {
                    if (ShouldSendParameter(parameters[i]))
                    {
                        cParams++;
                    }
                }
            }
            return cParams;
        }

        // Returns total number of parameters
        private static int GetParameterCount(SqlParameterCollection parameters)
        {
            return parameters != null ? parameters.Count : 0;
        }

        //
        // build the RPC record header for this stored proc and add parameters
        //
        private void BuildRPC(bool inSchema, SqlParameterCollection parameters, ref _SqlRPC rpc)
        {
            Debug.Assert(this.CommandType == System.Data.CommandType.StoredProcedure, "Command must be a stored proc to execute an RPC");
            int userParameterCount = CountSendableParameters(parameters);
            GetRPCObject(0, userParameterCount, ref rpc);

            rpc.ProcID = 0;

            // TDS Protocol allows rpc name with maximum length of 1046 bytes for ProcName
            // 4-part name 1 + 128 + 1 + 1 + 1 + 128 + 1 + 1 + 1 + 128 + 1 + 1 + 1 + 128 + 1 = 523
            // each char takes 2 bytes. 523 * 2 = 1046
            int commandTextLength = ADP.CharSize * CommandText.Length;
            if (commandTextLength <= MaxRPCNameLength)
            {
                rpc.rpcName = CommandText; // just get the raw command text
            }
            else
            {
                throw ADP.InvalidArgumentLength(nameof(CommandText), MaxRPCNameLength);
            }

            SetUpRPCParameters(rpc, inSchema, parameters);
        }

        //
        // build the RPC record header for sp_execute
        //
        // prototype for sp_execute is:
        // sp_execute(@handle int,param1value,param2value...)
        //
        private _SqlRPC BuildExecute(bool inSchema)
        {
            Debug.Assert(_prepareHandle != s_cachedInvalidPrepareHandle, "Invalid call to sp_execute without a valid handle!");

            const int systemParameterCount = 1;
            int userParameterCount = CountSendableParameters(_parameters);

            _SqlRPC rpc = null;
            GetRPCObject(systemParameterCount, userParameterCount, ref rpc);

            rpc.ProcID = TdsEnums.RPC_PROCID_EXECUTE;
            rpc.rpcName = TdsEnums.SP_EXECUTE;

            //@handle
            SqlParameter sqlParam = rpc.systemParams[0];
            sqlParam.SqlDbType = SqlDbType.Int;
            sqlParam.Size = 4;
            sqlParam.Value = _prepareHandle;
            sqlParam.Direction = ParameterDirection.Input;

            SetUpRPCParameters(rpc, inSchema, _parameters);
            return rpc;
        }

        //
        // build the RPC record header for sp_executesql and add the parameters
        //
        // prototype for sp_executesql is:
        // sp_executesql(@batch_text nvarchar(4000),@batch_params nvarchar(4000), param1,.. paramN)
        private void BuildExecuteSql(CommandBehavior behavior, string commandText, SqlParameterCollection parameters, ref _SqlRPC rpc)
        {

            Debug.Assert(_prepareHandle == s_cachedInvalidPrepareHandle, "This command has an existing handle, use sp_execute!");
            Debug.Assert(CommandType.Text == this.CommandType, "invalid use of sp_executesql for stored proc invocation!");
            int systemParamCount;
            SqlParameter sqlParam;

            int userParamCount = CountSendableParameters(parameters);
            if (userParamCount > 0)
            {
                systemParamCount = 2;
            }
            else
            {
                systemParamCount = 1;
            }

            GetRPCObject(systemParamCount, userParamCount, ref rpc);
            rpc.ProcID = TdsEnums.RPC_PROCID_EXECUTESQL;
            rpc.rpcName = TdsEnums.SP_EXECUTESQL;

            // @sql
            if (commandText == null)
            {
                commandText = GetCommandText(behavior);
            }
            sqlParam = rpc.systemParams[0];
            sqlParam.SqlDbType = ((commandText.Length << 1) <= TdsEnums.TYPE_SIZE_LIMIT) ? SqlDbType.NVarChar : SqlDbType.NText;
            sqlParam.Size = commandText.Length;
            sqlParam.Value = commandText;
            sqlParam.Direction = ParameterDirection.Input;

            if (userParamCount > 0)
            {
                string paramList = BuildParamList(_stateObj.Parser, _batchRPCMode ? parameters : _parameters);
                sqlParam = rpc.systemParams[1];
                sqlParam.SqlDbType = ((paramList.Length << 1) <= TdsEnums.TYPE_SIZE_LIMIT) ? SqlDbType.NVarChar : SqlDbType.NText;
                sqlParam.Size = paramList.Length;
                sqlParam.Value = paramList;
                sqlParam.Direction = ParameterDirection.Input;

                bool inSchema = (0 != (behavior & CommandBehavior.SchemaOnly));
                SetUpRPCParameters(rpc, inSchema, parameters);
            }
        }

        /// <summary>
        /// This function constructs a string parameter containing the exec statement in the following format
        /// N'EXEC sp_name @param1=@param1, @param1=@param2, ..., @paramN=@paramN'
        /// TODO: Need to handle return values.
        /// </summary>
        /// <param name="storedProcedureName">Stored procedure name</param>
        /// <param name="parameters">SqlParameter list</param>
        /// <returns>A string SqlParameter containing the constructed sql statement value</returns>
        private SqlParameter BuildStoredProcedureStatementForColumnEncryption(string storedProcedureName, SqlParameterCollection parameters)
        {
            Debug.Assert(CommandType == CommandType.StoredProcedure, "BuildStoredProcedureStatementForColumnEncryption() should only be called for stored procedures");
            Debug.Assert(!string.IsNullOrWhiteSpace(storedProcedureName), "storedProcedureName cannot be null or empty in BuildStoredProcedureStatementForColumnEncryption");

            StringBuilder execStatement = new StringBuilder();
            execStatement.Append(@"EXEC ");

            if (parameters is null)
            {
                execStatement.Append(ParseAndQuoteIdentifier(storedProcedureName, false));
                return new SqlParameter(
                    null,
                    ((execStatement.Length << 1) <= TdsEnums.TYPE_SIZE_LIMIT) ? SqlDbType.NVarChar : SqlDbType.NText,
                    execStatement.Length)
                {
                    Value = execStatement.ToString()
                };
            }

            // Find the return value parameter (if any).
            SqlParameter returnValueParameter = null;
            foreach (SqlParameter param in parameters)
            {
                if (param.Direction == ParameterDirection.ReturnValue)
                {
                    returnValueParameter = param;
                    break;
                }
            }

            // If there is a return value parameter we need to assign the result to it.
            // EXEC @returnValue = moduleName [parameters]
            if (returnValueParameter != null)
            {
                SqlParameter.AppendPrefixedParameterName(execStatement, returnValueParameter.ParameterName);
                execStatement.Append('=');
            }

            execStatement.Append(ParseAndQuoteIdentifier(storedProcedureName, false));

            // Build parameter list in the format
            // @param1=@param1, @param1=@param2, ..., @paramn=@paramn

            // Append the first parameter
            int index = 0;
            int count = parameters.Count;
            SqlParameter parameter;
            if (count > 0)
            {
                // Skip the return value parameters.
                while (index < parameters.Count && parameters[index].Direction == ParameterDirection.ReturnValue)
                {
                    index++;
                }

                if (index < count)
                {
                    parameter = parameters[index];
                    // Possibility of a SQL Injection issue through parameter names and how to construct valid identifier for parameters.
                    // Since the parameters comes from application itself, there should not be a security vulnerability.
                    // Also since the query is not executed, but only analyzed there is no possibility for elevation of privilege, but only for
                    // incorrect results which would only affect the user that attempts the injection.
                    execStatement.Append(' ');
                    SqlParameter.AppendPrefixedParameterName(execStatement, parameter.ParameterName);
                    execStatement.Append('=');
                    SqlParameter.AppendPrefixedParameterName(execStatement, parameter.ParameterName);

                    // InputOutput and Output parameters need to be marked as such.
                    if (parameter.Direction == ParameterDirection.Output ||
                        parameter.Direction == ParameterDirection.InputOutput)
                    {
                        execStatement.AppendFormat(@" OUTPUT");
                    }
                }
            }

            // Move to the next parameter.
            index++;

            // Append the rest of parameters
            for (; index < count; index++)
            {
                parameter = parameters[index];
                if (parameter.Direction != ParameterDirection.ReturnValue)
                {
                    execStatement.Append(", ");
                    SqlParameter.AppendPrefixedParameterName(execStatement, parameter.ParameterName);
                    execStatement.Append('=');
                    SqlParameter.AppendPrefixedParameterName(execStatement, parameter.ParameterName);

                    // InputOutput and Output parameters need to be marked as such.
                    if (
                        parameter.Direction == ParameterDirection.Output ||
                        parameter.Direction == ParameterDirection.InputOutput
                    )
                    {
                        execStatement.AppendFormat(@" OUTPUT");
                    }
                }
            }

            // Construct @tsql SqlParameter to be returned
            SqlParameter tsqlParameter = new SqlParameter(null, ((execStatement.Length << 1) <= TdsEnums.TYPE_SIZE_LIMIT) ? SqlDbType.NVarChar : SqlDbType.NText, execStatement.Length);
            tsqlParameter.Value = execStatement.ToString();

            return tsqlParameter;
        }

        // paramList parameter for sp_executesql, sp_prepare, and sp_prepexec
        internal string BuildParamList(TdsParser parser, SqlParameterCollection parameters, bool includeReturnValue = false)
        {
            StringBuilder paramList = new StringBuilder();
            bool fAddSeparator = false;

            int count = parameters.Count;
            for (int i = 0; i < count; i++)
            {
                SqlParameter sqlParam = parameters[i];
                sqlParam.Validate(i, CommandType.StoredProcedure == CommandType);
                // skip ReturnValue parameters; we never send them to the server
                if (!ShouldSendParameter(sqlParam, includeReturnValue))
                    continue;

                // add our separator for the ith parameter
                if (fAddSeparator)
                {
                    paramList.Append(',');
                }

                SqlParameter.AppendPrefixedParameterName(paramList, sqlParam.ParameterName);

                MetaType mt = sqlParam.InternalMetaType;

                //for UDTs, get the actual type name. Get only the typename, omit catalog and schema names.
                //in TSQL you should only specify the unqualified type name

                // paragraph above doesn't seem to be correct. Server won't find the type
                // if we don't provide a fully qualified name
                paramList.Append(" ");
                if (mt.SqlDbType == SqlDbType.Udt)
                {
                    string fullTypeName = sqlParam.UdtTypeName;
                    if (string.IsNullOrEmpty(fullTypeName))
                        throw SQL.MustSetUdtTypeNameForUdtParams();

                    paramList.Append(ParseAndQuoteIdentifier(fullTypeName, true /* is UdtTypeName */));
                }
                else if (mt.SqlDbType == SqlDbType.Structured)
                {
                    string typeName = sqlParam.TypeName;
                    if (string.IsNullOrEmpty(typeName))
                    {
                        throw SQL.MustSetTypeNameForParam(mt.TypeName, sqlParam.GetPrefixedParameterName());
                    }
                    paramList.Append(ParseAndQuoteIdentifier(typeName, false /* is not UdtTypeName*/));

                    // TVPs currently are the only Structured type and must be read only, so add that keyword
                    paramList.Append(" READONLY");
                }
                else
                {
                    // func will change type to that with a 4 byte length if the type has a two
                    // byte length and a parameter length > than that expressible in 2 bytes
                    mt = sqlParam.ValidateTypeLengths();
                    if ((!mt.IsPlp) && (sqlParam.Direction != ParameterDirection.Output))
                    {
                        sqlParam.FixStreamDataForNonPLP();
                    }
                    paramList.Append(mt.TypeName);
                }

                fAddSeparator = true;

                if (mt.SqlDbType == SqlDbType.Decimal)
                {
                    byte precision = sqlParam.GetActualPrecision();
                    byte scale = sqlParam.GetActualScale();

                    paramList.Append('(');

                    if (0 == precision)
                    {
                        precision = TdsEnums.DEFAULT_NUMERIC_PRECISION;
                    }

                    paramList.Append(precision);
                    paramList.Append(',');
                    paramList.Append(scale);
                    paramList.Append(')');
                }
                else if (mt.IsVarTime)
                {
                    byte scale = sqlParam.GetActualScale();

                    paramList.Append('(');
                    paramList.Append(scale);
                    paramList.Append(')');
                }
                else if (mt.SqlDbType == SqlDbTypeExtensions.Vector)
                {
                    // The validate function for SqlParameters would
                    // have already thrown InvalidCastException if an incompatible
                    // value is specified for SqlDbType Vector.
                    var sqlVectorProps = (ISqlVector)sqlParam.Value;
                    paramList.Append('(');
                    paramList.Append(sqlVectorProps.Length);
                    paramList.Append(')');
                }
                else if (!mt.IsFixed && !mt.IsLong && mt.SqlDbType != SqlDbType.Timestamp && mt.SqlDbType != SqlDbType.Udt && SqlDbType.Structured != mt.SqlDbType)
                {
                    int size = sqlParam.Size;

                    paramList.Append('(');

                    // if using non unicode types, obtain the actual byte length from the parser, with it's associated code page
                    if (mt.IsAnsiType)
                    {
                        object val = sqlParam.GetCoercedValue();
                        string s = null;

                        // deal with the sql types
                        if (val != null && (DBNull.Value != val))
                        {
                            s = (val as string);
                            if (s == null)
                            {
                                SqlString sval = val is SqlString ? (SqlString)val : SqlString.Null;
                                if (!sval.IsNull)
                                {
                                    s = sval.Value;
                                }
                            }
                        }

                        if (s != null)
                        {
                            int actualBytes = parser.GetEncodingCharLength(s, sqlParam.GetActualSize(), sqlParam.Offset, null);
                            // if actual number of bytes is greater than the user given number of chars, use actual bytes
                            if (actualBytes > size)
                                size = actualBytes;
                        }
                    }

                    // If the user specifies a 0-sized parameter for a variable len field
                    // pass over max size (8000 bytes or 4000 characters for wide types)
                    if (0 == size)
                        size = mt.IsSizeInCharacters ? (TdsEnums.MAXSIZE >> 1) : TdsEnums.MAXSIZE;

                    paramList.Append(size);
                    paramList.Append(')');
                }
                else if (mt.IsPlp && (mt.SqlDbType != SqlDbType.Xml) && (mt.SqlDbType != SqlDbType.Udt) && (mt.SqlDbType != SqlDbTypeExtensions.Json))
                {
                    paramList.Append("(max) ");
                }

                // set the output bit for Output or InputOutput parameters
                if (sqlParam.Direction != ParameterDirection.Input)
                    paramList.Append(" " + TdsEnums.PARAM_OUTPUT);
            }

            return paramList.ToString();
        }

        // Adds quotes to each part of a SQL identifier that may be multi-part, while leaving
        //  the result as a single composite name.
        private static string ParseAndQuoteIdentifier(string identifier, bool isUdtTypeName)
        {
            string[] strings = SqlParameter.ParseTypeName(identifier, isUdtTypeName);
            return ADP.BuildMultiPartName(strings);
        }

        private static string QuoteIdentifier(string[] strings, int offset, int length)
        {
            StringBuilder bld = new StringBuilder();

            // Stitching back together is a little tricky. Assume we want to build a full multi-part name
            //  with all parts except trimming separators for leading empty names (null or empty strings,
            //  but not whitespace). Separators in the middle should be added, even if the name part is 
            //  null/empty, to maintain proper location of the parts.
            for (int i = offset; i < (offset + length); i++)
            {
                if (0 < bld.Length)
                {
                    bld.Append('.');
                }
                if (strings[i] != null && 0 != strings[i].Length)
                {
                    ADP.AppendQuotedString(bld, "[", "]", strings[i]);
                }
            }

            return bld.ToString();
        }

        // returns set option text to turn on format only and key info on and off
        //  When we are executing as a text command, then we never need
        // to turn off the options since they command text is executed in the scope of sp_executesql.
        // For a stored proc command, however, we must send over batch sql and then turn off
        // the set options after we read the data.  See the code in Command.Execute()
        private string GetSetOptionsString(CommandBehavior behavior)
        {
            string s = null;

            if ((System.Data.CommandBehavior.SchemaOnly == (behavior & CommandBehavior.SchemaOnly)) ||
               (System.Data.CommandBehavior.KeyInfo == (behavior & CommandBehavior.KeyInfo)))
            {
                // SET FMTONLY ON will cause the server to ignore other SET OPTIONS, so turn
                // it off before we ask for browse mode metadata
                s = TdsEnums.FMTONLY_OFF;

                if (System.Data.CommandBehavior.KeyInfo == (behavior & CommandBehavior.KeyInfo))
                {
                    s = s + TdsEnums.BROWSE_ON;
                }

                if (System.Data.CommandBehavior.SchemaOnly == (behavior & CommandBehavior.SchemaOnly))
                {
                    s = s + TdsEnums.FMTONLY_ON;
                }
            }

            return s;
        }

        private string GetResetOptionsString(CommandBehavior behavior)
        {
            string s = null;

            // SET FMTONLY ON OFF
            if (System.Data.CommandBehavior.SchemaOnly == (behavior & CommandBehavior.SchemaOnly))
            {
                s = s + TdsEnums.FMTONLY_OFF;
            }

            // SET NO_BROWSETABLE OFF
            if (System.Data.CommandBehavior.KeyInfo == (behavior & CommandBehavior.KeyInfo))
            {
                s = s + TdsEnums.BROWSE_OFF;
            }

            return s;
        }

        private string GetCommandText(CommandBehavior behavior)
        {
            // build the batch string we send over, since we execute within a stored proc (sp_executesql), the SET options never need to be
            // turned off since they are scoped to the sproc
            Debug.Assert(System.Data.CommandType.Text == this.CommandType, "invalid call to GetCommandText for stored proc!");
            return GetSetOptionsString(behavior) + this.CommandText;
        }

        internal void CheckThrowSNIException()
        {
            var stateObj = _stateObj;
            if (stateObj != null)
            {
                stateObj.CheckThrowSNIException();
            }
        }

        // We're being notified that the underlying connection has closed
        internal void OnConnectionClosed()
        {
            var stateObj = _stateObj;
            if (stateObj != null)
            {
                stateObj.OnConnectionClosed();
            }
        }

        internal TdsParserStateObject StateObject
        {
            get
            {
                return _stateObj;
            }
        }

        private bool IsPrepared
        {
            get { return (_execType != EXECTYPE.UNPREPARED); }
        }

        private bool IsUserPrepared
        {
            get { return IsPrepared && !_hiddenPrepare && !IsDirty; }
        }

        internal bool IsDirty
        {
            get
            {
                // only dirty if prepared
                var activeConnection = _activeConnection;
                return (IsPrepared &&
                    (_dirty ||
                    ((_parameters != null) && (_parameters.IsDirty)) ||
                    ((activeConnection != null) && ((activeConnection.CloseCount != _preparedConnectionCloseCount) || (activeConnection.ReconnectCount != _preparedConnectionReconnectCount)))));
            }
            set
            {
                // only mark the command as dirty if it is already prepared
                // but always clear the value if it we are clearing the dirty flag
                _dirty = value ? IsPrepared : false;
                if (_parameters != null)
                {
                    _parameters.IsDirty = _dirty;
                }
                _cachedMetaData = null;
            }
        }

        /// <summary>
        /// Get or add to the number of records affected by SpDescribeParameterEncryption.
        /// The below line is used only for debug asserts and not exposed publicly or impacts functionality otherwise.
        /// </summary>
        internal int RowsAffectedByDescribeParameterEncryption
        {
            get
            {
                return _rowsAffectedBySpDescribeParameterEncryption;
            }
            set
            {
                if (-1 == _rowsAffectedBySpDescribeParameterEncryption)
                {
                    _rowsAffectedBySpDescribeParameterEncryption = value;
                }
                else if (0 < value)
                {
                    _rowsAffectedBySpDescribeParameterEncryption += value;
                }
            }
        }

        internal int InternalRecordsAffected
        {
            get
            {
                return _rowsAffected;
            }
            set
            {
                if (-1 == _rowsAffected)
                {
                    _rowsAffected = value;
                }
                else if (0 < value)
                {
                    _rowsAffected += value;
                }
            }
        }

        /// <summary>
        /// Clear the state in sqlcommand related to describe parameter encryption RPC requests.
        /// </summary>
        private void ClearDescribeParameterEncryptionRequests()
        {
            _sqlRPCParameterEncryptionReqArray = null;
            _currentlyExecutingDescribeParameterEncryptionRPC = 0;
            IsDescribeParameterEncryptionRPCCurrentlyInProgress = false;
            _rowsAffectedBySpDescribeParameterEncryption = -1;
        }

        internal void ClearBatchCommand()
        {
            _RPCList?.Clear();
            _currentlyExecutingBatch = 0;
        }

        internal void SetBatchRPCMode(bool value, int commandCount = 1)
        {
            _batchRPCMode = value;
            ClearBatchCommand();
            if (_batchRPCMode)
            {
                if (_RPCList == null)
                {
                    _RPCList = new List<_SqlRPC>(commandCount);
                }
                else
                {
                    _RPCList.Capacity = commandCount;
                }
            }
        }

        internal void SetBatchRPCModeReadyToExecute()
        {
            Debug.Assert(_batchRPCMode, "Command is not in batch RPC Mode");
            Debug.Assert(_RPCList != null, "No batch commands specified");

            _currentlyExecutingBatch = 0;
        }

        /// <summary>
        /// Set the column encryption setting to the new one.
        /// Do not allow conflicting column encryption settings.
        /// </summary>
        private void SetColumnEncryptionSetting(SqlCommandColumnEncryptionSetting newColumnEncryptionSetting)
        {
            if (!_wasBatchModeColumnEncryptionSettingSetOnce)
            {
                _columnEncryptionSetting = newColumnEncryptionSetting;
                _wasBatchModeColumnEncryptionSettingSetOnce = true;
            }
            else if (_columnEncryptionSetting != newColumnEncryptionSetting)
            {
                throw SQL.BatchedUpdateColumnEncryptionSettingMismatch();
            }
        }

        internal void AddBatchCommand(SqlBatchCommand batchCommand)
        {
            Debug.Assert(_batchRPCMode, "Command is not in batch RPC Mode");
            Debug.Assert(_RPCList != null);

            _SqlRPC rpc = new _SqlRPC
            {
                batchCommand = batchCommand
            };
            string commandText = batchCommand.CommandText;
            CommandType cmdType = batchCommand.CommandType;

            CommandText = commandText;
            CommandType = cmdType;

            // Set the column encryption setting.
            SetColumnEncryptionSetting(batchCommand.ColumnEncryptionSetting);

            GetStateObject();
            if (cmdType == CommandType.StoredProcedure)
            {
                BuildRPC(false, batchCommand.Parameters, ref rpc);
            }
            else
            {
                // All batch sql statements must be executed inside sp_executesql, including those without parameters
                BuildExecuteSql(CommandBehavior.Default, commandText, batchCommand.Parameters, ref rpc);
            }

            _RPCList.Add(rpc);

            ReliablePutStateObject();
        }

        internal int? GetRecordsAffected(int commandIndex)
        {
            Debug.Assert(_batchRPCMode, "Command is not in batch RPC Mode");
            Debug.Assert(_RPCList != null, "batch command have been cleared");
            return _RPCList[commandIndex].recordsAffected;
        }

        internal SqlBatchCommand GetCurrentBatchCommand()
        {
            if (_batchRPCMode)
            {
                return _RPCList[_currentlyExecutingBatch].batchCommand;
            }
            else
            {
                return _rpcArrayOf1?[0].batchCommand;
            }
        }

        internal SqlBatchCommand GetBatchCommand(int index)
        {
            return _RPCList[index].batchCommand;
        }

        internal int GetCurrentBatchIndex()
        {
            return _batchRPCMode ? _currentlyExecutingBatch : -1;
        }

        internal SqlException GetErrors(int commandIndex)
        {
            SqlException result = null;
            int length = (_RPCList[commandIndex].errorsIndexEnd - _RPCList[commandIndex].errorsIndexStart);
            if (0 < length)
            {
                SqlErrorCollection errors = new SqlErrorCollection();
                for (int i = _RPCList[commandIndex].errorsIndexStart; i < _RPCList[commandIndex].errorsIndexEnd; ++i)
                {
                    errors.Add(_RPCList[commandIndex].errors[i]);
                }
                for (int i = _RPCList[commandIndex].warningsIndexStart; i < _RPCList[commandIndex].warningsIndexEnd; ++i)
                {
                    errors.Add(_RPCList[commandIndex].warnings[i]);
                }
                result = SqlException.CreateException(errors, Connection.ServerVersion, Connection.ClientConnectionId, innerException: null, batchCommand: null);
            }
            return result;
        }

        private static void CancelIgnoreFailureCallback(object state)
        {
            SqlCommand command = (SqlCommand)state;
            command.CancelIgnoreFailure();
        }

        private void CancelIgnoreFailure()
        {
            // This method is used to route CancellationTokens to the Cancel method.
            // Cancellation is a suggestion, and exceptions should be ignored
            // rather than allowed to be unhandled, as there is no way to route
            // them to the caller.  It would be expected that the error will be
            // observed anyway from the regular method.  An example is cancelling
            // an operation on a closed connection.
            try
            {
                Cancel();
            }
            catch (Exception)
            {
            }
        }

        private void NotifyDependency()
        {
            if (_sqlDep != null)
            {
                _sqlDep.StartTimer(Notification);
            }
        }

        private void WriteBeginExecuteEvent()
        {
            SqlClientEventSource.Log.TryBeginExecuteEvent(ObjectID, Connection?.DataSource, Connection?.Database, CommandText, Connection?.ClientConnectionId);
        }

        /// <summary>
        /// Writes and end execute event in Event Source.
        /// </summary>
        /// <param name="success">True if SQL command finished successfully, otherwise false.</param>
        /// <param name="sqlExceptionNumber">Gets a number that identifies the type of error.</param>
        /// <param name="synchronous">True if SQL command was executed synchronously, otherwise false.</param>
        private void WriteEndExecuteEvent(bool success, int? sqlExceptionNumber, bool synchronous)
        {
            if (SqlClientEventSource.Log.IsExecutionTraceEnabled())
            {
                // SqlEventSource.WriteEvent(int, int, int, int) is faster than provided overload SqlEventSource.WriteEvent(int, object[]).
                // that's why trying to fit several booleans in one integer value

                // success state is stored the first bit in compositeState 0x01
                int successFlag = success ? 1 : 0;

                // isSqlException is stored in the 2nd bit in compositeState 0x100
                int isSqlExceptionFlag = sqlExceptionNumber.HasValue ? 2 : 0;

                // synchronous state is stored in the second bit in compositeState 0x10
                int synchronousFlag = synchronous ? 4 : 0;

                int compositeState = successFlag | isSqlExceptionFlag | synchronousFlag;

                SqlClientEventSource.Log.TryEndExecuteEvent(ObjectID, compositeState, sqlExceptionNumber.GetValueOrDefault(), Connection?.ClientConnectionId);
            }
        }
    }
}
