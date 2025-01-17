using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;
using System.Xml.Linq;

namespace Breeze.ContextProvider {
  // Base for EFContextProvider
  public abstract class ContextProvider {

    public IKeyGenerator KeyGenerator { get; set; }

    public static SaveOptions ExtractSaveOptions(dynamic dynSaveBundle) {
      var jsonSerializer = CreateJsonSerializer();

      var dynSaveOptions = dynSaveBundle.saveOptions;
      var saveOptions = (SaveOptions)jsonSerializer.Deserialize(new JTokenReader(dynSaveOptions), typeof(SaveOptions));
      return saveOptions;
    }

    public SaveOptions SaveOptions { get; set; }

    public string Metadata() {
      lock (_metadataLock) {
        if (_jsonMetadata == null) {
          _jsonMetadata = BuildJsonMetadata();
        }

        return _jsonMetadata;
      }
    }

    public static String XDocToJson(XDocument xDoc) {

      var sw = new StringWriter();
      using (var jsonWriter = new JsonPropertyFixupWriter(sw)) {
        // jsonWriter.Formatting = Newtonsoft.Json.Formatting.Indented;
        var jsonSerializer = new JsonSerializer();
        var converter = new XmlNodeConverter();
        jsonSerializer.Converters.Add(converter);
        jsonSerializer.Serialize(jsonWriter, xDoc);
      }

      var jsonText = sw.ToString();
      return jsonText;
    }

    protected void InitializeSaveState(JObject saveBundle)
    {
      JsonSerializer = CreateJsonSerializer();

      var dynSaveBundle = (dynamic)saveBundle;
      var entitiesArray = (JArray)dynSaveBundle.entities;
      var dynSaveOptions = dynSaveBundle.saveOptions;
      SaveOptions = (SaveOptions)JsonSerializer.Deserialize(new JTokenReader(dynSaveOptions), typeof(SaveOptions));
      SaveWorkState = new SaveWorkState(this, entitiesArray);
    }

    public SaveResult SaveChanges(JObject saveBundle, TransactionSettings transactionSettings = null) {

      if (SaveWorkState == null || SaveWorkState.WasUsed) {
        InitializeSaveState(saveBundle);
      }

      transactionSettings = transactionSettings ?? BreezeConfig.Instance.GetTransactionSettings();
      try {
        if (transactionSettings.TransactionType == TransactionType.TransactionScope) {
          var txOptions = transactionSettings.ToTransactionOptions();
          using (var txScope = new TransactionScope(TransactionScopeOption.Required, txOptions)) {           
            OpenAndSave(SaveWorkState);           
            txScope.Complete();
          }
        } else if (transactionSettings.TransactionType == TransactionType.DbTransaction) {
          this.OpenDbConnection();
          using (IDbTransaction tran = BeginTransaction(transactionSettings.IsolationLevelAs)) {
            try {
              OpenAndSave(SaveWorkState);
              tran.Commit();
            } catch {
              tran.Rollback();
              throw;
            }
          }          
        } else {
          OpenAndSave(SaveWorkState);
        }
      } catch (EntityErrorsException e) {
        SaveWorkState.EntityErrors = e.EntityErrors;
        throw; 
      } catch(Exception e2) {
        if (!HandleSaveException(e2, SaveWorkState)) {
          throw;
        }
      } finally {
        CloseDbConnection();
      }

      return SaveWorkState.ToSaveResult();
    }
    

    public Task<SaveResult> SaveChangesAsync(JObject saveBundle, TransactionSettings transactionSettings = null)
    {
      return SaveChangesAsync(saveBundle, CancellationToken.None, transactionSettings);
    }

    public async Task<SaveResult> SaveChangesAsync(JObject saveBundle, CancellationToken cancellationToken, TransactionSettings transactionSettings = null) {

    if (SaveWorkState == null || SaveWorkState.WasUsed) {
      InitializeSaveState(saveBundle);
    }

      transactionSettings = transactionSettings ?? BreezeConfig.Instance.GetTransactionSettings();
      try {
        if (transactionSettings.TransactionType == TransactionType.TransactionScope) {
          var txOptions = transactionSettings.ToTransactionOptions();
          using (var txScope = new TransactionScope(TransactionScopeOption.Required, txOptions)) {           
            await OpenAndSaveAsync(SaveWorkState, cancellationToken);           
            txScope.Complete();
          }
        } else if (transactionSettings.TransactionType == TransactionType.DbTransaction) {
          await this.OpenDbConnectionAsync(cancellationToken);
          using (IDbTransaction tran = BeginTransaction(transactionSettings.IsolationLevelAs)) {
            try {
              await OpenAndSaveAsync(SaveWorkState, cancellationToken);
              tran.Commit();
            } catch {
              tran.Rollback();
              throw;
            }
          }          
        } else {
          await OpenAndSaveAsync(SaveWorkState, cancellationToken);
        }
      } catch (EntityErrorsException e) {
        SaveWorkState.EntityErrors = e.EntityErrors;
        throw; 
      } catch(Exception e2) {
        if (!HandleSaveException(e2, SaveWorkState)) {
          throw;
        }
      } finally {
        await CloseDbConnectionAsync(cancellationToken);
      }

      return SaveWorkState.ToSaveResult();
    }

    // allows subclasses to plug in own save exception handling
    // either throw an exception here, return false or return true and modify the saveWorkState.
    protected virtual bool HandleSaveException(Exception e, SaveWorkState saveWorkState) {
      return false;
    }

    private void OpenAndSave(SaveWorkState saveWorkState) {
      
      OpenDbConnection();    // ensure connection is available for BeforeSaveEntities
      saveWorkState.BeforeSave();
      SaveChangesCore(saveWorkState);
      saveWorkState.AfterSave();
    }
    private async Task OpenAndSaveAsync(SaveWorkState saveWorkState, CancellationToken cancellationToken) {
      
      await OpenDbConnectionAsync(cancellationToken);    // ensure connection is available for BeforeSaveEntities
      await saveWorkState.BeforeSaveAsync(cancellationToken);
      await SaveChangesCoreAsync(saveWorkState, cancellationToken);
      await saveWorkState.AfterSaveAsync(cancellationToken);
    }

    

    private static JsonSerializer CreateJsonSerializer() {
      var serializerSettings = BreezeConfig.Instance.GetJsonSerializerSettingsForSave();
      var jsonSerializer = JsonSerializer.Create(serializerSettings);
      return jsonSerializer;
    }

    #region abstract and virtual methods

    /// <summary>
    /// Should only be called from BeforeSaveEntities and AfterSaveEntities.
    /// </summary>
    /// <returns>Open DbConnection used by the ContextProvider's implementation</returns>
    public abstract IDbConnection GetDbConnection();

    /// <summary>
    /// Should only be called from BeforeSaveEntities and AfterSaveEntities.
    /// Default implementation returns GetDbConnection()
    /// </summary>
    /// <returns>Open DbConnection used by the ContextProvider's implementation</returns>
    public virtual Task<IDbConnection> GetDbConnectionAsync()
    {
      return Task.FromResult(GetDbConnection());
    }
    /// <summary>
    /// Should only be called from BeforeSaveEntities and AfterSaveEntities.
    /// Default implementation returns GetDbConnection()
    /// </summary>
    /// <returns>Open DbConnection used by the ContextProvider's implementation</returns>
    public virtual Task<IDbConnection> GetDbConnectionAsync(CancellationToken cancellationToken)
    {
      return Task.FromResult(GetDbConnection());
    }
    /// <summary>
    /// Internal use only.  Should only be called by ContextProvider during SaveChanges.
    /// Opens the DbConnection used by the ContextProvider's implementation.
    /// Method must be idempotent; after it is called the first time, subsequent calls have no effect.
    /// </summary>
    protected abstract void OpenDbConnection();
    
    /// <summary>
    /// Internal use only.  Should only be called by ContextProvider during SaveChangesAsync.
    /// Opens the DbConnection used by the ContextProvider's implementation.
    /// Method must be idempotent; after it is called the first time, subsequent calls have no effect.
    /// Default implementation calls OpenDbConnection()
    /// </summary>
    protected virtual Task OpenDbConnectionAsync(CancellationToken cancellationToken)
    {
      OpenDbConnection();
      return Task.FromResult(0);
    }

    /// <summary>
    /// Internal use only.  Should only be called by ContextProvider during SaveChanges.
    /// Closes the DbConnection used by the ContextProvider's implementation.
    /// </summary>
    protected abstract void CloseDbConnection();

    /// <summary>
    /// Internal use only.  Should only be called by ContextProvider during SaveChangesAsync.
    /// Closes the DbConnection used by the ContextProvider's implementation.
    /// Default implementation calls CloseDbConnection()
    /// </summary>
    protected virtual Task CloseDbConnectionAsync(CancellationToken cancellationToken)
    {
      CloseDbConnection();
      return Task.FromResult(0);
    }

    protected virtual IDbTransaction BeginTransaction(System.Data.IsolationLevel isolationLevel) {
      var conn = GetDbConnection();
      if (conn == null) return null;
      return conn.BeginTransaction(isolationLevel);
    }

    protected abstract String BuildJsonMetadata();

    protected abstract void SaveChangesCore(SaveWorkState saveWorkState);

    /// <summary>
    /// The default implementation calls SavesChangesCore to maintain compatiblity with existing
    /// ContextProvider implementations.
    /// </summary>
    /// <param name="saveWorkState"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    protected virtual Task SaveChangesCoreAsync(SaveWorkState saveWorkState, CancellationToken cancellationToken)
    {
      SaveChangesCore(saveWorkState);
      return Task.FromResult(0);
    }

    public virtual object[] GetKeyValues(EntityInfo entityInfo) {
      throw new NotImplementedException();
    }

    protected virtual EntityInfo CreateEntityInfo() {
      return new EntityInfo();
    }

    public EntityInfo CreateEntityInfo(Object entity, EntityState entityState = EntityState.Added) {
      var ei = CreateEntityInfo();
      ei.Entity = entity;
      ei.EntityState = entityState;
      ei.ContextProvider = this;
      return ei;
    }


    public Func<EntityInfo, bool> BeforeSaveEntityDelegate { get; set; }
    public Func<EntityInfo, CancellationToken, Task<bool>> BeforeSaveEntityAsyncDelegate { get; set; }
    public Func<Dictionary<Type, List<EntityInfo>>, Dictionary<Type, List<EntityInfo>>> BeforeSaveEntitiesDelegate { get; set; }
    public Func<Dictionary<Type, List<EntityInfo>>, CancellationToken, Task<Dictionary<Type, List<EntityInfo>>>> BeforeSaveEntitiesAsyncDelegate { get; set; }
    public Action<Dictionary<Type, List<EntityInfo>>, List<KeyMapping>> AfterSaveEntitiesDelegate { get; set; }
    public Func<Dictionary<Type, List<EntityInfo>>, List<KeyMapping>,CancellationToken, Task> AfterSaveEntitiesAsyncDelegate { get; set; }

    /// <summary>
    /// The method is called for each entity to be saved before the save occurs.  If this method returns 'false'
    /// then the entity will be excluded from the save.  The base implementation returns the result of BeforeSaveEntityDelegate,
    /// or 'true' if BeforeSaveEntityDelegate is null.
    /// </summary>
    /// <param name="entityInfo"></param>
    /// <returns>true to include the entity in the save, false to exclude</returns>
    protected internal virtual bool BeforeSaveEntity(EntityInfo entityInfo) {
      if (BeforeSaveEntityDelegate != null) {
        return BeforeSaveEntityDelegate(entityInfo);
      } else {
        return true;
      }
    }

    /// <summary>
    /// The method is called for each entity to be saved before the save occurs during an async save.  If this method returns 'false'
    /// then the entity will be excluded from the save.  The base implementation returns the result of BeforeSaveEntityDelegate,
    /// or 'true' if BeforeSaveEntityDelegate is null.
    /// </summary>
    /// <param name="entityInfo"></param>
    /// <param name="cancellationToken"></param>
    /// <returns>true to include the entity in the save, false to exclude</returns>
    protected internal virtual Task<bool> BeforeSaveEntityAsync(EntityInfo entityInfo, CancellationToken cancellationToken) {
      if (BeforeSaveEntityAsyncDelegate != null) {
        return BeforeSaveEntityAsyncDelegate(entityInfo, cancellationToken);
      } else {
        return Task.FromResult(true);
      }
    }

    /// <summary>
    /// Called after BeforeSaveEntity, and before saving the entities to the persistence layer.
    /// Allows adding, changing, and removing entities prior to save.
    /// The base implementation returns the result of BeforeSaveEntitiesDelegate, or the unchanged
    /// saveMap if BeforeSaveEntitiesDelegate is null.
    /// </summary>
    /// <param name="saveMap">A List of EntityInfo for each Type</param>
    /// <returns>The EntityInfo for each entity that should be saved</returns>
    protected internal virtual Dictionary<Type, List<EntityInfo>> BeforeSaveEntities(Dictionary<Type, List<EntityInfo>> saveMap) {
      if (BeforeSaveEntitiesDelegate != null) {
        return BeforeSaveEntitiesDelegate(saveMap);
      } else {
        return saveMap;
      }
    }

    /// <summary>
    /// Called after BeforeSaveEntityAsync, and before saving the entities to the persistence layer.
    /// Allows adding, changing, and removing entities prior to save.
    /// The base implementation returns the result of BeforeSaveEntitiesDelegate, or the unchanged
    /// saveMap if BeforeSaveEntitiesDelegate is null.
    /// </summary>
    /// <param name="saveMap">A List of EntityInfo for each Type</param>
    /// <param name="cancellationToken"></param>
    /// <returns>The EntityInfo for each entity that should be saved</returns>
    protected internal virtual Task<Dictionary<Type, List<EntityInfo>>> BeforeSaveEntitiesAsync(Dictionary<Type, List<EntityInfo>> saveMap, CancellationToken cancellationToken) {
      if (BeforeSaveEntitiesAsyncDelegate != null) {
        return BeforeSaveEntitiesAsyncDelegate(saveMap, cancellationToken);
      } else {
        return Task.FromResult(saveMap);
      }
    }

    /// <summary>
    /// Called after the entities have been saved, and all the temporary keys have been replaced by real keys.
    /// The base implementation calls AfterSaveEntitiesDelegate, or does nothing if AfterSaveEntitiesDelegate is null.
    /// </summary>
    /// <param name="saveMap">The same saveMap that was returned from BeforeSaveEntities</param>
    /// <param name="keyMappings">The mapping of temporary keys to real keys</param>
    protected internal virtual void AfterSaveEntities(Dictionary<Type, List<EntityInfo>> saveMap, List<KeyMapping> keyMappings) {
      if (AfterSaveEntitiesDelegate != null) {
        AfterSaveEntitiesDelegate(saveMap, keyMappings);
      }
    }

    /// <summary>
    /// Called after the entities have been saved, and all the temporary keys have been replaced by real keys.
    /// The base implementation calls AfterSaveEntitiesDelegate, or does nothing if AfterSaveEntitiesDelegate is null.
    /// </summary>
    /// <param name="saveMap">The same saveMap that was returned from BeforeSaveEntities</param>
    /// <param name="keyMappings">The mapping of temporary keys to real keys</param>
    /// <param name="cancellationToken"></param>
    protected internal virtual async Task AfterSaveEntitiesAsync(Dictionary<Type, List<EntityInfo>> saveMap, List<KeyMapping> keyMappings, CancellationToken cancellationToken) {
      if (AfterSaveEntitiesAsyncDelegate != null) {
        await AfterSaveEntitiesAsyncDelegate(saveMap, keyMappings, cancellationToken);
      }
    }

    #endregion

    protected internal EntityInfo CreateEntityInfoFromJson(dynamic jo, Type entityType) {
      var entityInfo = CreateEntityInfo();

      entityInfo.Entity = JsonSerializer.Deserialize(new JTokenReader(jo), entityType);
      entityInfo.EntityState = (EntityState)Enum.Parse(typeof(EntityState), (String)jo.entityAspect.entityState);
      entityInfo.ContextProvider = this;


      entityInfo.UnmappedValuesMap = JsonToDictionary(jo.__unmapped);
      entityInfo.OriginalValuesMap = JsonToDictionary(jo.entityAspect.originalValuesMap);

      var autoGeneratedKey = jo.entityAspect.autoGeneratedKey;
      if (entityInfo.EntityState == EntityState.Added && autoGeneratedKey != null) {
        entityInfo.AutoGeneratedKey = new AutoGeneratedKey(entityInfo.Entity, autoGeneratedKey);
      }
      return entityInfo;
    }

    private Dictionary<String, Object> JsonToDictionary(dynamic json) {
      if (json == null) return null;
      var jprops = ((System.Collections.IEnumerable)json).Cast<JProperty>();
      var dict = jprops.ToDictionary(jprop => jprop.Name, jprop => {
        var val = jprop.Value as JValue;
        if (val != null) {
          return val.Value;
        } else if (jprop.Value as JArray != null){
          return jprop.Value as JArray;
        } else {
          return jprop.Value as JObject;
        }
      });
      return dict;
    }

    protected internal Type LookupEntityType(String entityTypeName) {
      var delims = new string[] { ":#" };
      var parts = entityTypeName.Split(delims, StringSplitOptions.None);
      var shortName = parts[0];
      var ns = parts[1];

      var typeName = ns + "." + shortName;
      var type = BreezeConfig.ProbeAssemblies
        .Select(a => a.GetType(typeName, false, true))
        .FirstOrDefault(t => t != null);
      if (type != null) {
        return type;
      } else {
        throw new ArgumentException("Assembly could not be found for " + entityTypeName);
      }
    }

    protected static Lazy<Type> KeyGeneratorType = new Lazy<Type>(() => {
      var typeCandidates = BreezeConfig.ProbeAssemblies.Concat(new Assembly[] { typeof(IKeyGenerator).Assembly })
       .SelectMany(a => a.GetTypes()).ToList();
      var generatorTypes = typeCandidates.Where(t => typeof(IKeyGenerator).IsAssignableFrom(t) && !t.IsAbstract)
        .ToList();
      if (generatorTypes.Count == 0) {
        throw new Exception("Unable to locate a KeyGenerator implementation.");
      }
      return generatorTypes.First();
    });

    protected SaveWorkState SaveWorkState  { get; private set; }
    protected JsonSerializer JsonSerializer { get; private set; }


    private object _metadataLock = new object();
    private string _jsonMetadata;

  }

  public class SaveWorkState {

    public SaveWorkState(ContextProvider contextProvider, JArray entitiesArray) {
      ContextProvider = contextProvider;
      var jObjects = entitiesArray.Select(jt => (dynamic)jt).ToList();
      var groups = jObjects.GroupBy(jo => (String)jo.entityAspect.entityTypeName).ToList();

      EntityInfoGroups = groups.Select(g => {
        var entityType = ContextProvider.LookupEntityType(g.Key);
        var entityInfos = g.Select(jo => ContextProvider.CreateEntityInfoFromJson(jo, entityType)).Cast<EntityInfo>().ToList();
        return new EntityGroup() { EntityType = entityType, EntityInfos = entityInfos };
      }).ToList();
    }

    public void BeforeSave() {
      SaveMap = new Dictionary<Type, List<EntityInfo>>();
      EntityInfoGroups.ForEach(eg => {
        var entityInfos = eg.EntityInfos.Where(ei => ContextProvider.BeforeSaveEntity(ei)).ToList();
        SaveMap.Add(eg.EntityType, entityInfos);
      });
      SaveMap = ContextProvider.BeforeSaveEntities(SaveMap);
      EntitiesWithAutoGeneratedKeys = SaveMap
        .SelectMany(eiGrp => eiGrp.Value)
        .Where(ei => ei.AutoGeneratedKey != null && ei.EntityState != EntityState.Detached)
        .ToList();
    }

    public Task BeforeSaveAsync()
    {
        return BeforeSaveAsync(CancellationToken.None);
    }

    public async Task BeforeSaveAsync(CancellationToken cancellationToken) {
      SaveMap = new Dictionary<Type, List<EntityInfo>>();
      foreach(var eg in EntityInfoGroups)
      {
        var entityInfos = new List<EntityInfo>();
        foreach(var ei in eg.EntityInfos)
        {
          if (ContextProvider.BeforeSaveEntity(ei) && await ContextProvider.BeforeSaveEntityAsync(ei, cancellationToken))
          {
            entityInfos.Add(ei);
          }
        }
        SaveMap.Add(eg.EntityType, entityInfos);
      }
      SaveMap = await ContextProvider.BeforeSaveEntitiesAsync(ContextProvider.BeforeSaveEntities(SaveMap), cancellationToken);
      
      EntitiesWithAutoGeneratedKeys = SaveMap
        .SelectMany(eiGrp => eiGrp.Value)
        .Where(ei => ei.AutoGeneratedKey != null && ei.EntityState != EntityState.Detached)
        .ToList();
    }

    public void AfterSave() {
      ContextProvider.AfterSaveEntities(SaveMap, KeyMappings);
    }

    public Task AfterSaveAsync()
    {
        return AfterSaveAsync(CancellationToken.None);
    }

    public Task AfterSaveAsync(CancellationToken cancellationToken) {
      ContextProvider.AfterSaveEntities(SaveMap, KeyMappings);
      return ContextProvider.AfterSaveEntitiesAsync(SaveMap, KeyMappings, cancellationToken);
    }

    public ContextProvider ContextProvider;
    protected List<EntityGroup> EntityInfoGroups;
    public Dictionary<Type, List<EntityInfo>> SaveMap { get; set; }
    public List<EntityInfo> EntitiesWithAutoGeneratedKeys { get; set; }
    public List<KeyMapping> KeyMappings;
    public List<EntityError> EntityErrors;
    public bool WasUsed { get; internal set; }

    public class EntityGroup {
      public Type EntityType;
      public List<EntityInfo> EntityInfos;
    }

    public SaveResult ToSaveResult() {
      WasUsed = true; // try to prevent reuse in subsequent SaveChanges
      if (EntityErrors != null) {
        return new SaveResult() { Errors = EntityErrors.Cast<Object>().ToList() };
      } else {
        var entities = SaveMap.SelectMany(kvp => kvp.Value.Where(ei => (ei.EntityState != EntityState.Detached))
          .Select(entityInfo => entityInfo.Entity)).ToList();
        var deletes = SaveMap.SelectMany(kvp => kvp.Value.Where(ei => (ei.EntityState == EntityState.Deleted || ei.EntityState == EntityState.Detached))
          .Select(entityInfo => new EntityKey(entityInfo.Entity, ContextProvider.GetKeyValues(entityInfo)))).ToList();
        return new SaveResult() { Entities = entities, KeyMappings = KeyMappings, DeletedKeys = deletes };
      }
    }
  }

  public class SaveOptions {
    public bool AllowConcurrentSaves { get; set; }
    public Object Tag { get; set; }
  }

  public interface IKeyGenerator {
    void UpdateKeys(List<TempKeyInfo> keys);
  }

  // instances of this sent to KeyGenerator
  public class TempKeyInfo {
    public TempKeyInfo(EntityInfo entityInfo) {
      _entityInfo = entityInfo;
    }
    public Object Entity {
      get { return _entityInfo.Entity; }
    }
    public Object TempValue {
      get { return _entityInfo.AutoGeneratedKey.TempValue; }
    }
    public Object RealValue {
      get { return _entityInfo.AutoGeneratedKey.RealValue; }
      set { _entityInfo.AutoGeneratedKey.RealValue = value; }
    }

    public PropertyInfo Property {
      get { return _entityInfo.AutoGeneratedKey.Property; }
    }

    private EntityInfo _entityInfo;

  }

  [Flags]
  public enum EntityState {
    Detached = 1,
    Unchanged = 2,
    Added = 4,
    Deleted = 8,
    Modified = 16,
  }

  public class EntityInfo {
    protected internal EntityInfo() {
    }

    public ContextProvider ContextProvider { get; internal set; }
    public Object Entity { get; internal set; }
    public EntityState EntityState { get;  set; }
    public Dictionary<String, Object> OriginalValuesMap { get;  set; }
    public bool ForceUpdate { get; set; }
    public AutoGeneratedKey AutoGeneratedKey { get; set; }
    public Dictionary<String, Object> UnmappedValuesMap { get; set; }
  }

  public enum AutoGeneratedKeyType {
    None,
    Identity,
    KeyGenerator
  }

  public class AutoGeneratedKey {
    public AutoGeneratedKey(Object entity, dynamic autoGeneratedKey) {
      Entity = entity;
      PropertyName = autoGeneratedKey.propertyName;
      AutoGeneratedKeyType = (AutoGeneratedKeyType)Enum.Parse(typeof(AutoGeneratedKeyType), (String)autoGeneratedKey.autoGeneratedKeyType);
      // TempValue and RealValue will be set later. - TempValue during Add, RealValue after save completes.
    }

    public Object Entity;
    public AutoGeneratedKeyType AutoGeneratedKeyType;
    public String PropertyName;
    public PropertyInfo Property {
      get {
        if (_property == null) {
          _property = Entity.GetType().GetProperty(PropertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }
        return _property;
      }
    }
    public Object TempValue;
    public Object RealValue;
    private PropertyInfo _property;
  }

  // Types returned to javascript as Json.
  public class SaveResult {
    public List<Object> Entities;
    public List<KeyMapping> KeyMappings;
    public List<EntityKey> DeletedKeys;
    public List<Object> Errors;
  }

  public class KeyMapping {
    public String EntityTypeName;
    public Object TempValue;
    public Object RealValue;
  }

  public class EntityKey {
    public EntityKey() {}
    public EntityKey(object entity, object key) {
      var t = entity.GetType();
      EntityTypeName = t.Name + ":#" + t.Namespace;
      KeyValue = key;
    }
    public String EntityTypeName;
    public Object KeyValue;
  }

    public class SaveError {
    public SaveError(IEnumerable<EntityError> entityErrors) {
      EntityErrors = entityErrors.ToList();
    }
    public SaveError(String message, IEnumerable<EntityError> entityErrors) {
      Message = message;
      EntityErrors = entityErrors.ToList();
    }
    public String Message { get; protected set; }
    public List<EntityError> EntityErrors { get; protected set; }
  }

  public class EntityErrorsException : Exception {
    public EntityErrorsException(IEnumerable<EntityError> entityErrors) {
      EntityErrors = entityErrors.ToList();
      StatusCode = HttpStatusCode.Forbidden;
    }

    public EntityErrorsException(String message, IEnumerable<EntityError> entityErrors)
      : base(message) {
      EntityErrors = entityErrors.ToList();
      StatusCode = HttpStatusCode.Forbidden;
    }


    public HttpStatusCode StatusCode { get; set; }
    public List<EntityError> EntityErrors { get; protected set; }
  }

  public class EntityError {
    
    public String ErrorName;
    public String EntityTypeName;
    public Object[] KeyValues;
    public String PropertyName;
    public string ErrorMessage;
    
  }

  

  public class JsonPropertyFixupWriter : JsonTextWriter {
    public JsonPropertyFixupWriter(TextWriter textWriter)
      : base(textWriter) {
      _isDataType = false;
    }

    public override void WritePropertyName(string name) {
      if (name.StartsWith("@")) {
        name = name.Substring(1);
      }
      name = ToCamelCase(name);
      _isDataType = name == "type";
      base.WritePropertyName(name);
    }

    public override void WriteValue(string value) {
      if (_isDataType && !value.StartsWith("Edm.")) {
        base.WriteValue("Edm." + value);
      } else {
        base.WriteValue(value);
      }
    }

    private static string ToCamelCase(string s) {
      if (string.IsNullOrEmpty(s) || !char.IsUpper(s[0])) {
        return s;
      }
      string str = char.ToLower(s[0], CultureInfo.InvariantCulture).ToString((IFormatProvider)CultureInfo.InvariantCulture);
      if (s.Length > 1) {
        str = str + s.Substring(1);
      }
      return str;
    }

    private bool _isDataType;



  }

}
