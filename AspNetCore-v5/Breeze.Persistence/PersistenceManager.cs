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
using System.Transactions;
using System.Xml.Linq;

namespace Breeze.Persistence {

  public abstract class PersistenceManager {

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

    protected void InitializeSaveState(JObject saveBundle) {
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
          // this.OpenDbConnection();
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
      } catch (Exception e2) {
        if (!HandleSaveException(e2, SaveWorkState)) {
          throw;
        }
      }
      finally {
        CloseDbConnection();
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
    /// Internal use only.  Should only be called by ContextProvider during SaveChanges.
    /// Opens the DbConnection used by the ContextProvider's implementation.
    /// Method must be idempotent; after it is called the first time, subsequent calls have no effect.
    /// </summary>
    protected abstract void OpenDbConnection();

    /// <summary>
    /// Internal use only.  Should only be called by ContextProvider during SaveChanges.
    /// Closes the DbConnection used by the ContextProvider's implementation.
    /// </summary>
    protected abstract void CloseDbConnection();

    protected virtual IDbTransaction BeginTransaction(System.Data.IsolationLevel isolationLevel) {
      var conn = GetDbConnection();
      if (conn == null) return null;
      return conn.BeginTransaction(isolationLevel);
    }

    protected abstract String BuildJsonMetadata();

    protected abstract void SaveChangesCore(SaveWorkState saveWorkState);

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

    /// <summary> If assigned, this function is called before each entity is saved.  If the function returns false, the entity will not be saved. </summary>
    public Func<EntityInfo, bool> BeforeSaveEntityDelegate { get; set; }
    /// <summary> If assigned, this function is called before entities are saved.  Entities in the dictionary can be added, removed, or changed before saving. </summary>
    public Func<Dictionary<Type, List<EntityInfo>>, Dictionary<Type, List<EntityInfo>>> BeforeSaveEntitiesDelegate { get; set; }
    /// <summary> If assigned, this function is called after all entities are saved. </summary>
    public Action<Dictionary<Type, List<EntityInfo>>, List<KeyMapping>> AfterSaveEntitiesDelegate { get; set; }

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
        } else if (jprop.Value as JArray != null) {
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

    protected SaveWorkState SaveWorkState { get; private set; }
    protected JsonSerializer JsonSerializer { get; private set; }


    private object _metadataLock = new object();
    private string _jsonMetadata;

  }

  public class SaveWorkState {

    public SaveWorkState(PersistenceManager contextProvider, JArray entitiesArray) {
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

    public void AfterSave() {
      ContextProvider.AfterSaveEntities(SaveMap, KeyMappings);
    }

    public PersistenceManager ContextProvider;
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

    /// <summary> Convert for sending to client </summary>
    public SaveResult ToSaveResult() {
      WasUsed = true; // try to prevent reuse in subsequent SaveChanges
      if (EntityErrors != null) {
        return new SaveResult() { Errors = EntityErrors.Cast<Object>().ToList() };
      } else {
        var entities = SaveMap.SelectMany(kvp => kvp.Value.Where(ei => (ei.EntityState != EntityState.Detached))
          .Select(entityInfo => entityInfo.Entity)).ToList();
        
        // we want to stub off any navigation properties here, but how to do it quickly.
        // entities.ForEach(e => e
        var deletes = SaveMap.SelectMany(kvp => kvp.Value.Where(ei => (ei.EntityState == EntityState.Deleted || ei.EntityState == EntityState.Detached))
          .Select(entityInfo => new EntityKey(entityInfo.Entity, ContextProvider.GetKeyValues(entityInfo)))).ToList();
        return new SaveResult() { Entities = entities, KeyMappings = KeyMappings, DeletedKeys = deletes };
      }
    }
  }

  /// <summary> Options passed from client </summary>
  public class SaveOptions {
    /// <summary> Not used on server </summary>
    public bool AllowConcurrentSaves { get; set; }
    /// <summary> Arbitrary object sent from client; may be used to influence save behavior </summary>
    public Object Tag { get; set; }
  }

  /// <summary> Server-side key generator </summary>
  public interface IKeyGenerator {
    /// <summary> Update the keys for the given entities </summary>
    void UpdateKeys(List<TempKeyInfo> keys);
  }

  /// <summary> Instances of this are sent to KeyGenerator  </summary>
  public class TempKeyInfo {
    /// <summary> Create for an entity </summary>
    public TempKeyInfo(EntityInfo entityInfo) {
      _entityInfo = entityInfo;
    }
    /// <summary> Entity (read-only) </summary>
    public Object Entity {
      get { return _entityInfo.Entity; }
    }
    /// <summary> Temp value of key from client (read-only)  </summary>
    public Object TempValue {
      get { return _entityInfo.AutoGeneratedKey.TempValue; }
    }
    /// <summary> New value of key, provided by KeyGenerator </summary>
    public Object RealValue {
      get { return _entityInfo.AutoGeneratedKey.RealValue; }
      set { _entityInfo.AutoGeneratedKey.RealValue = value; }
    }
    /// <summary> Property on the entity that holds the key </summary>
    public PropertyInfo Property {
      get { return _entityInfo.AutoGeneratedKey.Property; }
    }

    private EntityInfo _entityInfo;

  }

  /// <summary> The state of the entity </summary>
  [Flags]
  public enum EntityState {
    /// <summary> Not attached to a PersistenceManager </summary>
    Detached = 1,
    /// <summary> Not changed since retrieval from database </summary>
    Unchanged = 2,
    /// <summary> New entity not yet in database </summary>
    Added = 4,
    /// <summary> Existing entity to be deleted from database </summary>
    Deleted = 8,
    /// <summary> Existing entity to be modified in database </summary>
    Modified = 16,
  }
  /// <summary> All info for the entity on the server </summary>
  public class EntityInfo {
    /// <summary> Only created by PersistenceManager </summary>
    protected internal EntityInfo() {
    }
    /// <summary> PersistenceManager hosting this entity </summary>
    public PersistenceManager ContextProvider { get; internal set; }
    /// <summary> Entity instance </summary>
    public Object Entity { get; internal set; }
    /// <summary> State of the entity; changes during save </summary>
    public EntityState EntityState { get; set; }
    /// <summary> Original values of any changed properties (provided by client - not trustworthy) </summary>
    public Dictionary<String, Object> OriginalValuesMap { get; set; }
    /// <summary> Not used  </summary>
    public bool ForceUpdate { get; set; }
    /// <summary> AutoGeneratedKey (if any) associated with this entity </summary>
    public AutoGeneratedKey AutoGeneratedKey { get; set; }
    /// <summary> Properties passed from the client that cannot be mapped to server-side entity class </summary>
    public Dictionary<String, Object> UnmappedValuesMap { get; set; }
  }

  /// <summary> Types of key generation for new entities </summary>
  public enum AutoGeneratedKeyType {
    /// <summary> No server-generated key (keys are created on the client, e.g. using GUIDs) </summary>
    None,
    /// <summary> Keys generated in the database, e.g. using SQL Server IDENTITY column </summary>
    Identity,
    /// <summary> Keys generated on the server using a key-generation algorithm </summary>
    KeyGenerator
  }

  /// <summary> Server-generated keys for new entities </summary>
  public class AutoGeneratedKey {
    /// <summary> Create using information from the EntityAspect sent from the client. </summary>
    public AutoGeneratedKey(Object entity, dynamic autoGeneratedKey) {
      Entity = entity;
      PropertyName = autoGeneratedKey.propertyName;
      AutoGeneratedKeyType = (AutoGeneratedKeyType)Enum.Parse(typeof(AutoGeneratedKeyType), (String)autoGeneratedKey.autoGeneratedKeyType);
      // TempValue and RealValue will be set later. - TempValue during Add, RealValue after save completes.
    }
    /// <summary> Entity to which this key applies </summary>
    public Object Entity;
    /// <summary> Type of key generation </summary>
    public AutoGeneratedKeyType AutoGeneratedKeyType;
    /// <summary> Name of key property on the entity </summary>
    public String PropertyName;
    /// <summary> PropertyInfo of key property on the entity </summary>
    public PropertyInfo Property {
      get {
        if (_property == null) {
          _property = Entity.GetType().GetProperty(PropertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }
        return _property;
      }
    }
    /// <summary> Temporary value of the key, from the client </summary>
    public Object TempValue;
    /// <summary> Server-generated value of the key </summary>
    public Object RealValue;
    private PropertyInfo _property;
  }

  /// <summary> Type returned to client as JSON </summary>
  public class SaveResult {
    /// <summary> Entities affected by the save </summary>
    public List<Object> Entities;
    /// <summary> Map client temporary keys to server-generated keys </summary>
    public List<KeyMapping> KeyMappings;
    /// <summary> Identifies entities that were deleted on the server as part of the save </summary>
    public List<EntityKey> DeletedKeys;
    /// <summary> Errors that occurred during save </summary>
    public List<Object> Errors;
  }

  /// <summary> For server-generated keys.  Maps temporary key (from the client) to the real key (generated on the server) </summary>
  public class KeyMapping {
    /// <summary> Entity type (Name:#Namespace) </summary>
    public String EntityTypeName;
    /// <summary> Temporary key value (from the client) </summary>
    public Object TempValue;
    /// <summary> Real key value (generated on the server or in the database) </summary>
    public Object RealValue;
  }

  /// <summary> Unique identifier for an entity </summary>
  public class EntityKey {
    public EntityKey() { }
    public EntityKey(object entity, object key) {
      var t = entity.GetType();
      EntityTypeName = t.Name + ":#" + t.Namespace;
      KeyValue = key;
    }
    /// <summary> The C# class name (Name:#Namespace) of the entity type </summary>
    public String EntityTypeName;
    /// <summary> The key (id) value of the entity.  Maybe a single value or an array. </summary>
    public Object KeyValue;
  }

  //public class SaveError {
  //  public SaveError(IEnumerable<EntityError> entityErrors) {
  //    EntityErrors = entityErrors.ToList();
  //  }
  //  public SaveError(String message, IEnumerable<EntityError> entityErrors) {
  //    Message = message;
  //    EntityErrors = entityErrors.ToList();
  //  }
  //  public String Message { get; protected set; }
  //  public List<EntityError> EntityErrors { get; protected set; }
  //}

  /// <summary> Exception thrown during validation and save </summary>
  public class EntityErrorsException : Exception {
    /// <summary> Create with a collection of EntityErrors </summary>
    public EntityErrorsException(IEnumerable<EntityError> entityErrors) {
      EntityErrors = entityErrors.ToList();
      StatusCode = HttpStatusCode.Forbidden;
    }

    /// <summary> Create with a message and collection of EntityErrors </summary>
    public EntityErrorsException(String message, IEnumerable<EntityError> entityErrors)
      : base(message) {
      EntityErrors = entityErrors.ToList();
      StatusCode = HttpStatusCode.Forbidden;
    }

    /// <summary> Status to be returned to the client </summary>
    public HttpStatusCode StatusCode { get; set; }
    /// <summary> Errors causing the exception </summary>
    public List<EntityError> EntityErrors { get; protected set; }
  }

  /// <summary> Entity-specific error (such as validation error) that occur during save. </summary>
  public class EntityError {
    /// <summary> Short name of the error </summary>
    public String ErrorName;
    /// <summary> Entity type causing the error </summary>
    public String EntityTypeName;
    /// <summary> Identifier (if known) for the entity causing the error </summary>
    public Object[] KeyValues;
    /// <summary> Name of property (if known) causing the error </summary>
    public String PropertyName;
    /// <summary> Message describing the error </summary>
    public string ErrorMessage;
    /// <summary> Arbitrary object to pass to client </summary>
    public object Custom;
  }


  /// <summary> JsonTextWriter that alters property names coming from EF's mapping XML </summary>
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