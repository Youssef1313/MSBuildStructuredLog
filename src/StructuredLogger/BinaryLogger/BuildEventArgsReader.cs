﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Framework.Profiler;

namespace Microsoft.Build.Logging.StructuredLogger
{
    /// <summary>
    /// Deserializes and returns BuildEventArgs-derived objects from a BinaryReader
    /// </summary>
    internal class BuildEventArgsReader : IDisposable
    {
        private readonly BinaryReader binaryReader;
        private readonly int fileFormatVersion;

        // reflection is needed to set these three fields because public constructors don't provide
        // a way to set these from the outside
        private static FieldInfo buildEventArgsFieldThreadId =
            typeof(BuildEventArgs).GetField("threadId", BindingFlags.Instance | BindingFlags.NonPublic);
        private static FieldInfo buildEventArgsFieldSenderName =
            typeof(BuildEventArgs).GetField("senderName", BindingFlags.Instance | BindingFlags.NonPublic);
        private static FieldInfo buildEventArgsFieldTimestamp =
            typeof(BuildEventArgs).GetField("timestamp", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>
        /// Initializes a new instance of BuildEventArgsReader using a BinaryReader instance
        /// </summary>
        /// <param name="binaryReader">The BinaryReader to read BuildEventArgs from</param>
        /// <param name="fileFormatVersion">The file format version of the log file being read.</param>
        public BuildEventArgsReader(BinaryReader binaryReader, int fileFormatVersion)
        {
            this.binaryReader = binaryReader;
            this.fileFormatVersion = fileFormatVersion;
        }

        public void Dispose()
        {
            if (stringStorage != null)
            {
                stringStorage.Dispose();
                stringStorage = null;
            }
        }

        /// <summary>
        /// Raised when the log reader encounters a binary blob embedded in the stream.
        /// The arguments include the blob kind and the byte buffer with the contents.
        /// </summary>
        public event Action<BinaryLogRecordKind, byte[]> OnBlobRead;

        public event Action<string> OnStringRead;

        public event Action<IDictionary<string, string>> OnNameValueListRead;

        /// <summary>
        /// Reads the next log record from the binary reader. If there are no more records, returns null.
        /// </summary>
        public BuildEventArgs Read()
        {
            BinaryLogRecordKind recordKind = (BinaryLogRecordKind)ReadInt32();

            // Skip over data storage records since they don't result in a BuildEventArgs.
            // just ingest their data and continue.
            while (IsAuxiliaryRecord(recordKind))
            {
                // these are ordered by commonality
                if (recordKind == BinaryLogRecordKind.String)
                {
                    ReadStringRecord();
                }
                else if (recordKind == BinaryLogRecordKind.NameValueList)
                {
                    ReadNameValueList();
                }
                else if (recordKind == BinaryLogRecordKind.ProjectImportArchive)
                {
                    ReadBlob(recordKind);
                }

                recordKind = (BinaryLogRecordKind)ReadInt32();
            }

            BuildEventArgs result = null;
            switch (recordKind)
            {
                case BinaryLogRecordKind.EndOfFile:
                    break;
                case BinaryLogRecordKind.BuildStarted:
                    result = ReadBuildStartedEventArgs();
                    break;
                case BinaryLogRecordKind.BuildFinished:
                    result = ReadBuildFinishedEventArgs();
                    break;
                case BinaryLogRecordKind.ProjectStarted:
                    result = ReadProjectStartedEventArgs();
                    break;
                case BinaryLogRecordKind.ProjectFinished:
                    result = ReadProjectFinishedEventArgs();
                    break;
                case BinaryLogRecordKind.TargetStarted:
                    result = ReadTargetStartedEventArgs();
                    break;
                case BinaryLogRecordKind.TargetFinished:
                    result = ReadTargetFinishedEventArgs();
                    break;
                case BinaryLogRecordKind.TaskStarted:
                    result = ReadTaskStartedEventArgs();
                    break;
                case BinaryLogRecordKind.TaskFinished:
                    result = ReadTaskFinishedEventArgs();
                    break;
                case BinaryLogRecordKind.Error:
                    result = ReadBuildErrorEventArgs();
                    break;
                case BinaryLogRecordKind.Warning:
                    result = ReadBuildWarningEventArgs();
                    break;
                case BinaryLogRecordKind.Message:
                    result = ReadBuildMessageEventArgs();
                    break;
                case BinaryLogRecordKind.CriticalBuildMessage:
                    result = ReadCriticalBuildMessageEventArgs();
                    break;
                case BinaryLogRecordKind.TaskCommandLine:
                    result = ReadTaskCommandLineEventArgs();
                    break;
                case BinaryLogRecordKind.ProjectEvaluationStarted:
                    result = ReadProjectEvaluationStartedEventArgs();
                    break;
                case BinaryLogRecordKind.ProjectEvaluationFinished:
                    result = ReadProjectEvaluationFinishedEventArgs();
                    break;
                case BinaryLogRecordKind.ProjectImported:
                    result = ReadProjectImportedEventArgs();
                    break;
                case BinaryLogRecordKind.TargetSkipped:
                    result = ReadTargetSkippedEventArgs();
                    break;
                case BinaryLogRecordKind.EnvironmentVariableRead:
                    result = ReadEnvironmentVariableReadEventArgs();
                    break;
                case BinaryLogRecordKind.PropertyReassignment:
                    result = ReadPropertyReassignmentEventArgs();
                    break;
                case BinaryLogRecordKind.UninitializedPropertyRead:
                    result = ReadUninitializedPropertyReadEventArgs();
                    break;
                case BinaryLogRecordKind.PropertyInitialValueSet:
                    result = ReadPropertyInitialValueSetEventArgs();
                    break;
                default:
                    break;
            }

            return result;
        }

        private static bool IsAuxiliaryRecord(BinaryLogRecordKind recordKind)
        {
            return recordKind == BinaryLogRecordKind.ProjectImportArchive
                || recordKind == BinaryLogRecordKind.NameValueList
                || recordKind == BinaryLogRecordKind.String;
        }

        private void ReadBlob(BinaryLogRecordKind kind)
        {
            int length = ReadInt32();
            byte[] bytes = binaryReader.ReadBytes(length);
            OnBlobRead?.Invoke(kind, bytes);
        }

        private readonly List<IDictionary<string, string>> nameValueLists = new List<IDictionary<string, string>>();
        private readonly List<object> stringRecords = new List<object>();
        private StringStorage stringStorage = new StringStorage();

        private void ReadNameValueList()
        {
            var list = new Dictionary<string, string>();

            int count = ReadInt32();
            for (int i = 0; i < count; i++)
            {
                string key = ReadDeduplicatedString();
                string value = ReadDeduplicatedString();
                list[key] = value;
            }

            nameValueLists.Add(list);
            OnNameValueListRead?.Invoke(list);
        }

        private void ReadStringRecord()
        {
            string text = ReadString();
            object storedString = stringStorage.Add(text);
            stringRecords.Add(storedString);
            OnStringRead?.Invoke(text);
        }

        private BuildEventArgs ReadProjectImportedEventArgs()
        {
            var fields = ReadBuildEventArgsFields();
            // Read unused Importance, it defaults to Low
            ReadInt32();

            bool importIgnored = false;

            // the ImportIgnored field was introduced in file format version 3
            if (fileFormatVersion > 2)
            {
                importIgnored = ReadBoolean();
            }

            var importedProjectFile = ReadOptionalString();
            var unexpandedProject = ReadOptionalString();

            var e = new ProjectImportedEventArgs(
                fields.LineNumber,
                fields.ColumnNumber,
                fields.Message);

            SetCommonFields(e, fields);

            e.ProjectFile = fields.ProjectFile;

            e.ImportedProjectFile = importedProjectFile;
            e.UnexpandedProject = unexpandedProject;
            e.ImportIgnored = importIgnored;
            return e;
        }

        private BuildEventArgs ReadTargetSkippedEventArgs()
        {
            var fields = ReadBuildEventArgsFields();
            // Read unused Importance, it defaults to Low
            ReadInt32();
            var targetFile = ReadOptionalString();
            var targetName = ReadOptionalString();
            var parentTarget = ReadOptionalString();
            var buildReason = (TargetBuiltReason)ReadInt32();

            var e = new TargetSkippedEventArgs(
                fields.Message);

            SetCommonFields(e, fields);

            e.ProjectFile = fields.ProjectFile;
            e.TargetFile = targetFile;
            e.TargetName = targetName;
            e.ParentTarget = parentTarget;
            e.BuildReason = buildReason;

            return e;
        }

        private BuildEventArgs ReadBuildStartedEventArgs()
        {
            var fields = ReadBuildEventArgsFields();
            var environment = ReadStringDictionary();

            var e = new BuildStartedEventArgs(
                fields.Message,
                fields.HelpKeyword,
                environment);
            SetCommonFields(e, fields);
            return e;
        }

        private BuildEventArgs ReadBuildFinishedEventArgs()
        {
            var fields = ReadBuildEventArgsFields();
            var succeeded = ReadBoolean();

            var e = new BuildFinishedEventArgs(
                fields.Message,
                fields.HelpKeyword,
                succeeded,
                fields.Timestamp);
            SetCommonFields(e, fields);
            return e;
        }

        private BuildEventArgs ReadProjectEvaluationStartedEventArgs()
        {
            var fields = ReadBuildEventArgsFields();
            var projectFile = ReadDeduplicatedString();

            var e = new ProjectEvaluationStartedEventArgs(fields.Message)
            {
                ProjectFile = projectFile
            };
            SetCommonFields(e, fields);
            return e;
        }

        private BuildEventArgs ReadProjectEvaluationFinishedEventArgs()
        {
            var fields = ReadBuildEventArgsFields();
            var projectFile = ReadDeduplicatedString();

            var e = new ProjectEvaluationFinishedEventArgs(fields.Message)
            {
                ProjectFile = projectFile
            };
            SetCommonFields(e, fields);

            // ProfilerResult was introduced in version 5
            if (fileFormatVersion > 4)
            {
                var hasProfileData = ReadBoolean();
                if (hasProfileData)
                {
                    var count = ReadInt32();

                    var d = new Dictionary<EvaluationLocation, ProfiledLocation>(count);
                    for (int i = 0; i < count; i++)
                    {
                        var evaluationLocation = ReadEvaluationLocation();
                        var profiledLocation = ReadProfiledLocation();
                        d[evaluationLocation] = profiledLocation;
                    }

                    e.ProfilerResult = new ProfilerResult(d);
                }
            }

            return e;
        }

        private BuildEventArgs ReadProjectStartedEventArgs()
        {
            var fields = ReadBuildEventArgsFields();
            BuildEventContext parentContext = null;
            if (ReadBoolean())
            {
                parentContext = ReadBuildEventContext();
            }

            var projectFile = ReadOptionalString();
            var projectId = ReadInt32();
            var targetNames = ReadDeduplicatedString();
            var toolsVersion = ReadOptionalString();

            IDictionary<string, string> globalProperties = null;

            if (fileFormatVersion > 6)
            {
                if (ReadBoolean())
                {
                    globalProperties = ReadStringDictionary();
                }
            }

            var propertyList = ReadPropertyList();
            var itemList = ReadItems();

            var e = new ProjectStartedEventArgs(
                projectId,
                fields.Message,
                fields.HelpKeyword,
                projectFile,
                targetNames,
                propertyList,
                itemList,
                parentContext,
                globalProperties,
                toolsVersion);
            SetCommonFields(e, fields);
            return e;
        }

        private BuildEventArgs ReadProjectFinishedEventArgs()
        {
            var fields = ReadBuildEventArgsFields();
            var projectFile = ReadOptionalString();
            var succeeded = ReadBoolean();

            var e = new ProjectFinishedEventArgs(
                fields.Message,
                fields.HelpKeyword,
                projectFile,
                succeeded,
                fields.Timestamp);
            SetCommonFields(e, fields);
            return e;
        }

        private BuildEventArgs ReadTargetStartedEventArgs()
        {
            var fields = ReadBuildEventArgsFields();
            var targetName = ReadOptionalString();
            var projectFile = ReadOptionalString();
            var targetFile = ReadOptionalString();
            var parentTarget = ReadOptionalString();
            // BuildReason was introduced in version 4
            var buildReason = fileFormatVersion > 3 ? (TargetBuiltReason) ReadInt32() : TargetBuiltReason.None;

            var e = new TargetStartedEventArgs(
                fields.Message,
                fields.HelpKeyword,
                targetName,
                projectFile,
                targetFile,
                parentTarget,
                buildReason,
                fields.Timestamp);
            SetCommonFields(e, fields);
            return e;
        }

        private BuildEventArgs ReadTargetFinishedEventArgs()
        {
            var fields = ReadBuildEventArgsFields();
            var succeeded = ReadBoolean();
            var projectFile = ReadOptionalString();
            var targetFile = ReadOptionalString();
            var targetName = ReadOptionalString();
            var targetOutputItemList = ReadTaskItemList();

            var e = new TargetFinishedEventArgs(
                fields.Message,
                fields.HelpKeyword,
                targetName,
                projectFile,
                targetFile,
                succeeded,
                fields.Timestamp,
                targetOutputItemList);
            SetCommonFields(e, fields);
            return e;
        }

        private BuildEventArgs ReadTaskStartedEventArgs()
        {
            var fields = ReadBuildEventArgsFields();
            var taskName = ReadOptionalString();
            var projectFile = ReadOptionalString();
            var taskFile = ReadOptionalString();

            var e = new TaskStartedEventArgs(
                fields.Message,
                fields.HelpKeyword,
                projectFile,
                taskFile,
                taskName,
                fields.Timestamp);
            SetCommonFields(e, fields);
            return e;
        }

        private BuildEventArgs ReadTaskFinishedEventArgs()
        {
            var fields = ReadBuildEventArgsFields();
            var succeeded = ReadBoolean();
            var taskName = ReadOptionalString();
            var projectFile = ReadOptionalString();
            var taskFile = ReadOptionalString();

            var e = new TaskFinishedEventArgs(
                fields.Message,
                fields.HelpKeyword,
                projectFile,
                taskFile,
                taskName,
                succeeded,
                fields.Timestamp);
            SetCommonFields(e, fields);
            return e;
        }

        private BuildEventArgs ReadBuildErrorEventArgs()
        {
            var fields = ReadBuildEventArgsFields();
            ReadDiagnosticFields(fields);

            var e = new BuildErrorEventArgs(
                fields.Subcategory,
                fields.Code,
                fields.File,
                fields.LineNumber,
                fields.ColumnNumber,
                fields.EndLineNumber,
                fields.EndColumnNumber,
                fields.Message,
                fields.HelpKeyword,
                fields.SenderName,
                fields.Timestamp);
            e.BuildEventContext = fields.BuildEventContext;
            e.ProjectFile = fields.ProjectFile;
            return e;
        }

        private BuildEventArgs ReadBuildWarningEventArgs()
        {
            var fields = ReadBuildEventArgsFields();
            ReadDiagnosticFields(fields);

            var e = new BuildWarningEventArgs(
                fields.Subcategory,
                fields.Code,
                fields.File,
                fields.LineNumber,
                fields.ColumnNumber,
                fields.EndLineNumber,
                fields.EndColumnNumber,
                fields.Message,
                fields.HelpKeyword,
                fields.SenderName,
                fields.Timestamp);
            e.BuildEventContext = fields.BuildEventContext;
            e.ProjectFile = fields.ProjectFile;
            return e;
        }

        private BuildEventArgs ReadBuildMessageEventArgs()
        {
            var fields = ReadBuildEventArgsFields();
            var importance = (MessageImportance)ReadInt32();

            var e = new BuildMessageEventArgs(
                fields.Subcategory,
                fields.Code,
                fields.File,
                fields.LineNumber,
                fields.ColumnNumber,
                fields.EndLineNumber,
                fields.EndColumnNumber,
                fields.Message,
                fields.HelpKeyword,
                fields.SenderName,
                importance,
                fields.Timestamp);
            e.BuildEventContext = fields.BuildEventContext;
            e.ProjectFile = fields.ProjectFile;
            return e;
        }

        private BuildEventArgs ReadTaskCommandLineEventArgs()
        {
            var fields = ReadBuildEventArgsFields();
            var importance = (MessageImportance)ReadInt32();
            var commandLine = ReadOptionalString();
            var taskName = ReadOptionalString();

            var e = new TaskCommandLineEventArgs(
                commandLine,
                taskName,
                importance,
                fields.Timestamp);
            e.BuildEventContext = fields.BuildEventContext;
            e.ProjectFile = fields.ProjectFile;
            return e;
        }

        private BuildEventArgs ReadCriticalBuildMessageEventArgs()
        {
            var fields = ReadBuildEventArgsFields();
            var importance = (MessageImportance)ReadInt32();

            var e = new CriticalBuildMessageEventArgs(
                fields.Subcategory,
                fields.Code,
                fields.File,
                fields.LineNumber,
                fields.ColumnNumber,
                fields.EndLineNumber,
                fields.EndColumnNumber,
                fields.Message,
                fields.HelpKeyword,
                fields.SenderName,
                fields.Timestamp);
            e.BuildEventContext = fields.BuildEventContext;
            e.ProjectFile = fields.ProjectFile;
            return e;
        }

        private BuildEventArgs ReadEnvironmentVariableReadEventArgs()
        {
            var fields = ReadBuildEventArgsFields();
            var importance = (MessageImportance)ReadInt32();

            var environmentVariableName = ReadDeduplicatedString();

            var e = new EnvironmentVariableReadEventArgs(
                environmentVariableName,
                fields.Message,
                fields.HelpKeyword,
                fields.SenderName,
                importance);
            SetCommonFields(e, fields);

            return e;
        }

        private BuildEventArgs ReadPropertyReassignmentEventArgs()
        {
            var fields = ReadBuildEventArgsFields();
            var importance = (MessageImportance)ReadInt32();
            string propertyName = ReadDeduplicatedString();
            string previousValue = ReadDeduplicatedString();
            string newValue = ReadDeduplicatedString();
            string location = ReadDeduplicatedString();

            var e = new PropertyReassignmentEventArgs(
                propertyName,
                previousValue,
                newValue,
                location,
                fields.Message,
                fields.HelpKeyword,
                fields.SenderName,
                importance);
            SetCommonFields(e, fields);

            return e;
        }

        private BuildEventArgs ReadUninitializedPropertyReadEventArgs()
        {
            var fields = ReadBuildEventArgsFields();
            var importance = (MessageImportance)ReadInt32();
            string propertyName = ReadDeduplicatedString();

            var e = new UninitializedPropertyReadEventArgs(
                propertyName,
                fields.Message,
                fields.HelpKeyword,
                fields.SenderName,
                importance);
            SetCommonFields(e, fields);

            return e;
        }

        private BuildEventArgs ReadPropertyInitialValueSetEventArgs()
        {
            var fields = ReadBuildEventArgsFields();
            var importance = (MessageImportance)ReadInt32();
            string propertyName = ReadDeduplicatedString();
            string propertyValue = ReadDeduplicatedString();
            string propertySource = ReadDeduplicatedString();

            var e = new PropertyInitialValueSetEventArgs(
                propertyName,
                propertyValue,
                propertySource,
                fields.Message,
                fields.HelpKeyword,
                fields.SenderName,
                importance);
            SetCommonFields(e, fields);

            return e;
        }

        /// <summary>
        /// For errors and warnings these 8 fields are written out explicitly
        /// (their presence is not marked as a bit in the flags). So we have to
        /// read explicitly.
        /// </summary>
        /// <param name="fields"></param>
        private void ReadDiagnosticFields(BuildEventArgsFields fields)
        {
            fields.Subcategory = ReadOptionalString();
            fields.Code = ReadOptionalString();
            fields.File = ReadOptionalString();
            fields.ProjectFile = ReadOptionalString();
            fields.LineNumber = ReadInt32();
            fields.ColumnNumber = ReadInt32();
            fields.EndLineNumber = ReadInt32();
            fields.EndColumnNumber = ReadInt32();
        }

        private BuildEventArgsFields ReadBuildEventArgsFields()
        {
            BuildEventArgsFieldFlags flags = (BuildEventArgsFieldFlags)ReadInt32();
            var result = new BuildEventArgsFields();
            result.Flags = flags;

            if ((flags & BuildEventArgsFieldFlags.Message) != 0)
            {
                result.Message = ReadDeduplicatedString();
            }

            if ((flags & BuildEventArgsFieldFlags.BuildEventContext) != 0)
            {
                result.BuildEventContext = ReadBuildEventContext();
            }

            if ((flags & BuildEventArgsFieldFlags.ThreadId) != 0)
            {
                result.ThreadId = ReadInt32();
            }

            if ((flags & BuildEventArgsFieldFlags.HelpHeyword) != 0)
            {
                result.HelpKeyword = ReadDeduplicatedString();
            }

            if ((flags & BuildEventArgsFieldFlags.SenderName) != 0)
            {
                result.SenderName = ReadDeduplicatedString();
            }

            if ((flags & BuildEventArgsFieldFlags.Timestamp) != 0)
            {
                result.Timestamp = ReadDateTime();
            }

            if ((flags & BuildEventArgsFieldFlags.Subcategory) != 0)
            {
                result.Subcategory = ReadDeduplicatedString();
            }

            if ((flags & BuildEventArgsFieldFlags.Code) != 0)
            {
                result.Code = ReadDeduplicatedString();
            }

            if ((flags & BuildEventArgsFieldFlags.File) != 0)
            {
                result.File = ReadDeduplicatedString();
            }

            if ((flags & BuildEventArgsFieldFlags.ProjectFile) != 0)
            {
                result.ProjectFile = ReadDeduplicatedString();
            }

            if ((flags & BuildEventArgsFieldFlags.LineNumber) != 0)
            {
                result.LineNumber = ReadInt32();
            }

            if ((flags & BuildEventArgsFieldFlags.ColumnNumber) != 0)
            {
                result.ColumnNumber = ReadInt32();
            }

            if ((flags & BuildEventArgsFieldFlags.EndLineNumber) != 0)
            {
                result.EndLineNumber = ReadInt32();
            }

            if ((flags & BuildEventArgsFieldFlags.EndColumnNumber) != 0)
            {
                result.EndColumnNumber = ReadInt32();
            }

            return result;
        }

        private void SetCommonFields(BuildEventArgs buildEventArgs, BuildEventArgsFields fields)
        {
            buildEventArgs.BuildEventContext = fields.BuildEventContext;

            if ((fields.Flags & BuildEventArgsFieldFlags.ThreadId) != 0)
            {
                buildEventArgsFieldThreadId.SetValue(buildEventArgs, fields.ThreadId);
            }

            if ((fields.Flags & BuildEventArgsFieldFlags.SenderName) != 0)
            {
                buildEventArgsFieldSenderName.SetValue(buildEventArgs, fields.SenderName);
            }

            if ((fields.Flags & BuildEventArgsFieldFlags.Timestamp) != 0)
            {
                buildEventArgsFieldTimestamp.SetValue(buildEventArgs, fields.Timestamp);
            }
        }

        private ArrayList ReadPropertyList()
        {
            var properties = ReadStringDictionary();
            if (properties == null)
            {
                return null;
            }

            var list = new ArrayList();
            foreach (var property in properties)
            {
                var entry = new DictionaryEntry(property.Key, property.Value);
                list.Add(entry);
            }

            return list;
        }

        private BuildEventContext ReadBuildEventContext()
        {
            int nodeId = ReadInt32();
            int projectContextId = ReadInt32();
            int targetId = ReadInt32();
            int taskId = ReadInt32();
            int submissionId = ReadInt32();
            int projectInstanceId = ReadInt32();

            // evaluationId was introduced in format version 2
            int evaluationId = BuildEventContext.InvalidEvaluationId;
            if (fileFormatVersion > 1)
            {
                evaluationId = ReadInt32();
            }

            var result = new BuildEventContext(
                submissionId,
                nodeId,
                evaluationId,
                projectInstanceId,
                projectContextId,
                targetId,
                taskId);
            return result;
        }

        private IDictionary<string, string> ReadStringDictionary()
        {
            if (fileFormatVersion < 10)
            {
                return ReadLegacyStringDictionary();
            }

            int index = ReadInt32();
            if (index == 0)
            {
                return null;
            }

            var record = GetNameValueList(index);
            if (record != null)
            {
                return record;
            }

            return null;
        }

        private IDictionary<string, string> ReadLegacyStringDictionary()
        {
            int count = ReadInt32();
            if (count == 0)
            {
                return null;
            }

            var result = new Dictionary<string, string>(count);

            for (int i = 0; i < count; i++)
            {
                string key = ReadString();
                string value = ReadString();
                result[key] = value;
            }

            return result;
        }

        private class TaskItem : ITaskItem
        {
            private static readonly Dictionary<string, string> emptyMetadata = new Dictionary<string, string>();

            public string ItemSpec { get; set; }
            public IDictionary<string, string> Metadata { get; }

            public TaskItem()
            {
                Metadata = new Dictionary<string, string>();
            }

            public TaskItem(string itemSpec, IDictionary<string, string> metadata)
            {
                ItemSpec = itemSpec;
                Metadata = metadata ?? emptyMetadata;
            }

            public int MetadataCount => Metadata.Count;

            public ICollection MetadataNames => (ICollection)Metadata.Keys;

            public IDictionary CloneCustomMetadata()
            {
                return (IDictionary)Metadata;
            }

            public void CopyMetadataTo(ITaskItem destinationItem)
            {
                throw new NotImplementedException();
            }

            public string GetMetadata(string metadataName)
            {
                return Metadata[metadataName];
            }

            public void RemoveMetadata(string metadataName)
            {
                throw new NotImplementedException();
            }

            public void SetMetadata(string metadataName, string metadataValue)
            {
                throw new NotImplementedException();
            }

            public override string ToString()
            {
                return $"{ItemSpec} Metadata: {MetadataCount}";
            }
        }

        private IDictionary<string, string> GetNameValueList(int id)
        {
            id -= BuildEventArgsWriter.NameValueRecordStartIndex;
            if (id >= 0 && id < this.nameValueLists.Count)
            {
                var list = this.nameValueLists[id];
                return list;
            }

            return new Dictionary<string, string>();
        }

        private ITaskItem ReadTaskItem()
        {
            string itemSpec = ReadDeduplicatedString();
            var metadata = ReadStringDictionary();

            var taskItem = new TaskItem(itemSpec, metadata);
            return taskItem;
        }

        private IEnumerable ReadItems()
        {
            int count = ReadInt32();
            if (count == 0)
            {
                return null;
            }

            List<DictionaryEntry> list;
            if (fileFormatVersion < 10)
            {
                list = new List<DictionaryEntry>(count);
                for (int i = 0; i < count; i++)
                {
                    string key = ReadString();
                    ITaskItem item = ReadTaskItem();
                    list.Add(new DictionaryEntry(key, item));
                }
            }
            else
            {
                list = new List<DictionaryEntry>();
                for (int i = 0; i < count; i++)
                {
                    string itemName = ReadDeduplicatedString();
                    var items = ReadTaskItemList();
                    foreach (var item in items)
                    {
                        list.Add(new DictionaryEntry(itemName, item));
                    }
                }
            }

            return list;
        }

        private IEnumerable ReadTaskItemList()
        {
            int count = ReadInt32();
            if (count == 0)
            {
                return null;
            }

            var list = new List<ITaskItem>(count);

            for (int i = 0; i < count; i++)
            {
                ITaskItem item = ReadTaskItem();
                list.Add(item);
            }

            return list;
        }

        private string ReadString()
        {
            return binaryReader.ReadString();
        }

        private string ReadOptionalString()
        {
            if (fileFormatVersion < 10)
            {
                if (ReadBoolean())
                {
                    return ReadString();
                }
                else
                {
                    return null;
                }
            }

            return ReadDeduplicatedString();
        }

        private string ReadDeduplicatedString()
        {
            if (fileFormatVersion < 10)
            {
                return ReadString();
            }

            int index = ReadInt32();
            if (index == 0)
            {
                return null;
            }
            else if (index == 1)
            {
                return string.Empty;
            }

            // we reserve numbers 2-9 for future use.
            // the writer assigns 10 as the index of the first string
            index -= BuildEventArgsWriter.StringStartIndex;
            if (index >= 0 && index < this.stringRecords.Count)
            {
                object storedString = stringRecords[index];
                string result = stringStorage.Get(storedString);
                return result;
            }

            return string.Empty;
        }

        private int ReadInt32()
        {
            return Read7BitEncodedInt(binaryReader);
        }

        private long ReadInt64()
        {
            return binaryReader.ReadInt64();
        }

        private bool ReadBoolean()
        {
            return binaryReader.ReadBoolean();
        }

        private DateTime ReadDateTime()
        {
            return new DateTime(binaryReader.ReadInt64(), (DateTimeKind)ReadInt32());
        }

        private TimeSpan ReadTimeSpan()
        {
            return new TimeSpan(binaryReader.ReadInt64());
        }

        private int Read7BitEncodedInt(BinaryReader reader)
        {
            // Read out an Int32 7 bits at a time.  The high bit
            // of the byte when on means to continue reading more bytes.
            int count = 0;
            int shift = 0;
            byte b;
            do
            {
                // Check for a corrupted stream.  Read a max of 5 bytes.
                // In a future version, add a DataFormatException.
                if (shift == 5 * 7)  // 5 bytes max per Int32, shift += 7
                {
                    throw new FormatException();
                }

                // ReadByte handles end of stream cases for us.
                b = reader.ReadByte();
                count |= (b & 0x7F) << shift;
                shift += 7;
            } while ((b & 0x80) != 0);
            return count;
        }

        private ProfiledLocation ReadProfiledLocation()
        {
            var numberOfHits = ReadInt32();
            var exclusiveTime = ReadTimeSpan();
            var inclusiveTime = ReadTimeSpan();

            return new ProfiledLocation(inclusiveTime, exclusiveTime, numberOfHits);
        }

        private EvaluationLocation ReadEvaluationLocation()
        {
            var elementName = ReadOptionalString();
            var description = ReadOptionalString();
            var evaluationDescription = ReadOptionalString();
            var file = ReadOptionalString();
            var kind = (EvaluationLocationKind)ReadInt32();
            var evaluationPass = (EvaluationPass)ReadInt32();

            int? line = null;
            var hasLine = ReadBoolean();
            if (hasLine)
            {
                line = ReadInt32(); 
            }

            // Id and parent Id were introduced in version 6
            if (fileFormatVersion > 5)
            {
                var id = ReadInt64();
                long? parentId = null;
                var hasParent = ReadBoolean();
                if (hasParent)
                {
                    parentId = ReadInt64();
                }

                return new EvaluationLocation(id, parentId, evaluationPass, evaluationDescription, file, line, elementName, description, kind);
            }

            return new EvaluationLocation(0, null, evaluationPass, evaluationDescription, file, line, elementName, description, kind);
        }

        internal class StringPosition
        {
            public long FilePosition;
            public int StringLength;
        }

        internal class StringStorage : IDisposable
        {
            private string filePath;
            private FileStream stream;
            private StreamWriter streamWriter;
            private StreamReader streamReader;
            private StringBuilder stringBuilder;

            public const int StringSizeThreshold = 1024;

            public StringStorage()
            {
                if (!Environment.Is64BitProcess)
                {
                    filePath = Path.GetTempFileName();
                    var utf8noBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                    stream = new FileStream(
                        filePath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        bufferSize: 4096,
                        FileOptions.RandomAccess | FileOptions.DeleteOnClose);
                    streamWriter = new StreamWriter(stream, utf8noBom, 65536);
                    streamWriter.AutoFlush = true;
                    streamReader = new StreamReader(stream, utf8noBom);
                    stringBuilder = new StringBuilder();
                }
            }

            public object Add(string text)
            {
                if (filePath == null || text.Length <= StringSizeThreshold)
                {
                    return text;
                }

                var stringPosition = new StringPosition();

                stringPosition.FilePosition = stream.Position;

                streamWriter.Write(text);

                stringPosition.StringLength = text.Length;
                return stringPosition;
            }

            public string Get(object storedString)
            {
                if (storedString is string text)
                {
                    return text;
                }

                var position = (StringPosition)storedString;

                stream.Position = position.FilePosition;
                stringBuilder.Length = position.StringLength;
                for (int i = 0; i < position.StringLength; i++)
                {
                    char ch = (char)streamReader.Read();
                    stringBuilder[i] = ch;
                }

                stream.Position = stream.Length;

                string result = stringBuilder.ToString();
                stringBuilder.Clear();
                return result;
            }

            public void Dispose()
            {
                try
                {
                    if (streamWriter != null)
                    {
                        streamWriter.Dispose();
                        streamWriter = null;
                    }

                    if (stream != null)
                    {
                        stream.Dispose();
                        stream = null;
                    }
                }
                catch
                {
                }
            }
        }
    }
}
