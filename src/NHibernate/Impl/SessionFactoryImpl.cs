using System;
using System.Collections;
using System.Data;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Text;
using System.Xml;

using NHibernate.Cache;
using NHibernate.Connection;
using NHibernate.Cfg;
using NHibernate.Collection;
using NHibernate.Dialect;
using NHibernate.Engine;
using NHibernate.Hql;
using NHibernate.Id;
using NHibernate.Mapping;
using NHibernate.Metadata;
using NHibernate.Persister;
using NHibernate.Transaction;
using NHibernate.Type;
using NHibernate.Util;

using HibernateDialect = NHibernate.Dialect.Dialect;

namespace NHibernate.Impl 
{
	/// <summary>
	///  Concrete implementation of a SessionFactory.
	/// </summary>
	/// <remarks>
	/// Has the following responsibilities:
	/// <list type="">
	/// <item>
	/// Caches configuration settings (immutably)</item>
	/// <item>
	/// Caches "compiled" mappings - ie. <see cref="IClassPersisters"/> 
	/// and <see cref="CollectionPersisters"/>
	/// </item>
	/// <item>
	/// Caches "compiled" queries (memory sensitive cache)
	/// </item>
	/// <item>
	/// Manages <c>PreparedStatements/IDbCommands</c> - how true in NH?
	/// </item>
	/// <item>
	/// Delegates <c>IDbConnection</c> management to the <see cref="IConnectionProvider"/>
	/// </item>
	/// <item>
	/// Factory for instances of <see cref="ISession"/>
	/// </item>
	/// </list>
	/// <para>
	/// This class must appear immutable to clients, even if it does all kinds of caching
	/// and pooling under the covers.  It is crucial that the class is not only thread safe
	/// , but also highly concurrent.  Synchronization must be used extremely sparingly.
	/// </para>
	/// </remarks>
	[Serializable]
	internal class SessionFactoryImpl : ISessionFactory, ISessionFactoryImplementor, IObjectReference 
	{
		
		private static readonly log4net.ILog log = log4net.LogManager.GetLogger(typeof(SessionFactoryImpl));

		[NonSerialized] private Settings settings;
		private string name;
		private string uuid;

		[NonSerialized] private IDictionary classPersisters;
		[NonSerialized] private IDictionary classPersistersByName;
		[NonSerialized] private IDictionary collectionPersisters;
		[NonSerialized] private IDictionary namedQueries;
		[NonSerialized] private IDictionary imports;
		[NonSerialized] private IDictionary properties;
		// TODO: figure out why this is commented out in nh and not h2.0.3
		//[NonSerialized] private Templates templates;
		[NonSerialized] private IInterceptor interceptor;

		private static IIdentifierGenerator uuidgen = new UUIDHexGenerator();
	
		public SessionFactoryImpl(Configuration cfg, IDictionary properties, IInterceptor interceptor, Settings settings) 
		{
			log.Info("building session factory");
			if ( log.IsDebugEnabled ) 
			{
				StringBuilder sb = new StringBuilder("instantiating session factory with properties: ");
				foreach(DictionaryEntry entry in properties) 
				{
					sb.AppendFormat("{0}={1};", entry.Key, ((string)entry.Key).IndexOf("connection_string")>0?"***":entry.Value);
				}
				log.Debug(sb.ToString());
			}

			this.interceptor = interceptor;
			this.properties = properties;
			this.settings = settings;

			// Persisters:

			classPersisters = new Hashtable();
			classPersistersByName = new Hashtable();

			foreach(PersistentClass model in cfg.ClassMappings) 
			{
				System.Type persisterClass = model.Persister;
				IClassPersister cp;
				cp = PersisterFactory.Create(model, this);
				classPersisters[model.PersistentClazz] = cp;
				
				// Adds the "Namespace.ClassName" (FullClassname) as a lookup to get to the Persiter.
				// Most of the internals of NHibernate use this method to get to the Persister since
				// Model.Name is used in so many places.  It would be nice to fix it up to be Model.TypeName
				// instead of just FullClassname
				classPersistersByName[model.Name] = cp ;
				
				// Add in the AssemblyQualifiedName (includes version) as a lookup to get to the Persister.  
				// In HQL the Imports are used to get from the Classname to the Persister.  The
				// Imports provide the ability to jump from the Classname to the AssemblyQualifiedName.
				classPersistersByName[model.PersistentClazz.AssemblyQualifiedName] = cp;
			}

			collectionPersisters = new Hashtable();
			foreach( Mapping.Collection map in cfg.CollectionMappings ) 
			{
				collectionPersisters[map.Role] = new CollectionPersister(map, cfg, this) ;
			}

			foreach(IClassPersister persister in classPersisters.Values) 
			{
				persister.PostInstantiate(this);
			}

			//TODO: Add for databinding


			// serialization info
			name = settings.SessionFactoryName;
			try 
			{
				uuid = (string) uuidgen.Generate(null, null);
			} 
			catch (Exception) 
			{
				throw new AssertionFailure("could not generate UUID");
			}

			SessionFactoryObjectFactory.AddInstance(uuid, name, this, properties);

			namedQueries = cfg.NamedQueries;
			imports = new Hashtable( cfg.Imports );

			log.Debug("Instantiated session factory");

		}

		// Emulates constant time LRU/MRU algorithms for cache
		// It is better to hold strong references on some (LRU/MRU) queries
		private const int MaxStrongRefCount = 128;
		[NonSerialized] private readonly object[] strongRefs = new object[MaxStrongRefCount];
		[NonSerialized] private int strongRefIndex = 0;
		[NonSerialized] private readonly IDictionary softQueryCache = new Hashtable(); //TODO: make soft reference map

		
		static SessionFactoryImpl() 
		{
			// used to do some CGLIB stuff in here for QueryKeyFactory and FilterKeyFactory
		}
																												

		/// <summary>
		/// A class that can be used as a Key in a Hashtable for 
		/// a Query Cache.
		/// </summary>
		private class QueryCacheKey 
		{
			private string _query;
			private bool _scalar;

			internal QueryCacheKey(string query, bool scalar) 
			{
				_query = query;
				_scalar = scalar;
			}

			public string Query 
			{
				get { return _query; }
			}

			public bool Scalar 
			{
				get { return _scalar; }
			}

			#region System.Object Members

			public override bool Equals(object obj)
			{
				QueryCacheKey other = obj as QueryCacheKey;
				if( other==null) return false;

				return Equals(other);
			}

			public bool Equals(QueryCacheKey obj) 
			{
				return this.Query.Equals(obj.Query) && this.Scalar==obj.Scalar;
			}

			public override int GetHashCode()
			{
				unchecked 
				{
					return this.Query.GetHashCode() + this.Scalar.GetHashCode();
				}
			}

			#endregion


		}
		
		/// <summary>
		/// A class that can be used as a Key in a Hashtable for 
		/// a Query Cache.
		/// </summary>
		private class FilterCacheKey 
		{
			private string _role;
			private string _query;
			private bool _scalar;

			internal FilterCacheKey(string role, string query, bool scalar) 
			{
				_role = role;
				_query = query;
				_scalar = scalar;
			}

			public string Role 
			{
				get { return _role; }
			}

			public string Query 
			{
				get { return _query; }
			}

			public bool Scalar 
			{
				get { return _scalar; }
			}

			#region System.Object Members

			public override bool Equals(object obj)
			{
				FilterCacheKey other = obj as FilterCacheKey;
				if( other==null) return false;

				return Equals(other);
			}

			public bool Equals(FilterCacheKey obj) 
			{
				return this.Role.Equals(obj.Role) && this.Query.Equals(obj.Query) && this.Scalar==obj.Scalar;
			}

			public override int GetHashCode()
			{
				unchecked 
				{
					return this.Role.GetHashCode() + this.Query.GetHashCode() + this.Scalar.GetHashCode();
				}
			}

			#endregion


		}
		

		[MethodImpl(MethodImplOptions.Synchronized)]
		private object Get(object key) 
		{		
			object result = softQueryCache[key];
			if ( result != null ) 
			{
				strongRefs[ ++strongRefIndex % MaxStrongRefCount ] = result;
			}
			return result;
		}

		[MethodImpl(MethodImplOptions.Synchronized)]
		private void Put(object key, object value) 
		{
			softQueryCache[key] = value;
			strongRefs[ ++strongRefIndex % MaxStrongRefCount ] = value;
		}

		public IConnectionProvider ConnectionProvider 
		{
			get {return settings.ConnectionProvider;}
		}

		public IsolationLevel Isolation 
		{
			get { return settings.IsolationLevel; }
		}

		public QueryTranslator GetQuery(string query) 
		{
			return GetQuery(query, false);
		}

		public QueryTranslator GetShallowQuery(string query) 
		{
			return GetQuery(query, true);
		}

		private QueryTranslator GetQuery(string query, bool shallow) 
		{
			QueryCacheKey cacheKey = new QueryCacheKey(query, shallow);

			// have to be careful to ensure that if the JVM does out-of-order execution
			// then another thread can't get an uncompiled QueryTranslator from the cache
			// we also have to be very careful to ensure that other threads can perform
			// compiled queries while another query is being compiled

			QueryTranslator q = (QueryTranslator) Get(cacheKey);
			if ( q==null) 
			{
				q = new QueryTranslator( Dialect );
				Put(cacheKey, q);
			}
			
			q.Compile(this, query, settings.QuerySubstitutions, shallow);
			
			return q;
		}

		public FilterTranslator GetFilter(string query, string collectionRole, bool scalar) 
		{
			FilterCacheKey cacheKey = new FilterCacheKey( collectionRole, query, scalar );

			FilterTranslator q = (FilterTranslator) Get(cacheKey);
			if ( q==null ) 
			{
				q = new FilterTranslator( Dialect );
				Put(cacheKey, q);
			}
			
			q.Compile(collectionRole, this, query, settings.QuerySubstitutions, scalar);
			
			return q;
		}

		private ISession OpenSession(IDbConnection connection, bool autoClose, long timestamp, IInterceptor interceptor) 
		{
			return new SessionImpl( connection, this, autoClose, timestamp, interceptor );
		}

		public ISession OpenSession(IDbConnection connection, IInterceptor interceptor) 
		{
			//TODO: figure out why autoClose was set to false - diff in JDBC vs ADO.NET???
			return OpenSession( connection, false, long.MinValue, interceptor );
		}

		public ISession OpenSession(IInterceptor interceptor) 
		{
			long timestamp = Timestamper.Next();
			return OpenSession( null, true, timestamp, interceptor );
		}

		public ISession OpenSession(IDbConnection connection) 
		{
			return OpenSession(connection, interceptor);
		}

		public ISession OpenSession() 
		{
			return OpenSession(interceptor);
		}

		public IDbConnection OpenConnection() 
		{
			try 
			{
				return ConnectionProvider.GetConnection();
			} 
			catch (Exception sqle) 
			{
				throw new ADOException("cannot open connection", sqle);
			}
		}
		
		public void CloseConnection(IDbConnection conn) 
		{
			try 
			{
				ConnectionProvider.CloseConnection(conn);
			} 
			catch (Exception e) 
			{
				throw new ADOException("cannot close connection", e);
			}
		}

		public IClassPersister GetPersister(string className) 
		{
			return GetPersister( className, true );
		}

		public IClassPersister GetPersister(string className, bool throwException) 
		{
			IClassPersister result = classPersistersByName[className] as IClassPersister;
			
			if ( result==null && throwException ) 
			{
				throw new MappingException( "No persister for: " + className );
			}
			return result;
		}

		public IClassPersister GetPersister(System.Type theClass) 
		{
			IClassPersister result = classPersisters[theClass] as IClassPersister;
			if ( result==null) throw new MappingException( "No persisters for: " + theClass.FullName );
			return result;
		}

		public CollectionPersister GetCollectionPersister(string role) 
		{
			CollectionPersister result = collectionPersisters[role] as CollectionPersister;
			if ( result==null ) throw new MappingException( "No persisters for collection role: " + role );
			return result;
		}

		public IDatabinder OpenDatabinder() 
		{
			throw new NotImplementedException("Have not coded Databinder yet.");
		}

		public Dialect.Dialect Dialect 
		{
			get { return settings.Dialect; }
		}

		private ITransactionFactory BuildTransactionFactory(IDictionary transactionProps) 
		{
			return new TransactionFactory();
		}

		public ITransactionFactory TransactionFactory 
		{
			get { return settings.TransactionFactory; }
		}

//		public bool UseAdoBatch 
//		{
//			get { return adoBatchSize > 0; }
//		}

//		public int ADOBatchSize {
//			get { return adoBatchSize; }
//		}

		public bool EnableJoinedFetch 
		{
			get { return settings.IsOuterJoinFetchEnabled; }
		}

//		public bool UseScrollableResultSets {
//			get { return useScrollableResultSets; }
//		}

		public string GetNamedQuery(string name) 
		{
			string queryString = namedQueries[name] as string;
			if (queryString==null) throw new MappingException("Named query not known: " + name);
			return queryString;
		}

		public IType GetIdentifierType(System.Type objectClass) 
		{
			return GetPersister(objectClass).IdentifierType;
		}

		#region System.Runtime.Serialization.IObjectReference Members

		public object GetRealObject(StreamingContext context)
		{
			// the SessionFactory that was serialized only has values in the properties
			// "name" and "uuid".  In here convert the serialized SessionFactory into
			// an instance of the SessionFactory in the current AppDomain.
			log.Debug("Resolving serialized SessionFactory");
			
			// look for the instance by uuid - this will work when a SessionFactory
			// is serialized and deserialized in the same AppDomain.
			ISessionFactory result = SessionFactoryObjectFactory.GetInstance(uuid);
			if(result==null) 
			{
				// if we were deserialized into a different AppDomain, look for an instance with the
				// same name.
				result = SessionFactoryObjectFactory.GetNamedInstance(name);
				if(result==null) 
				{
					throw new NullReferenceException("Could not find a SessionFactory named " + name + " or identified by uuid " + uuid );
				}
				else 
				{
					log.Debug("resolved SessionFactory by name");
				}
			}
			else 
			{
				log.Debug("resolved SessionFactory by uuid");
			}

			return result;
		}

		#endregion


		public IType[] GetReturnTypes(string queryString) 
		{
			string[] queries = QueryTranslator.ConcreteQueries(queryString, this);
			if ( queries.Length==0 ) throw new HibernateException("Query does not refer to any persistent classes: " + queryString);
			return GetShallowQuery( queries[0] ).ReturnTypes;
		}

		public ICollection GetNamedParameters(string queryString) 
		{
			string[] queries = QueryTranslator.ConcreteQueries(queryString, this);
			if ( queries.Length==0 ) throw new HibernateException("Query does not refer to any persistent classes: " + queryString);
			return GetShallowQuery( queries[0] ).NamedParameters;
		}

		public string DefaultSchema 
		{
			get { return settings.DefaultSchemaName; }
		}

//		public void SetFetchSize(IDbCommand statement) 
//		{
//			if ( settings.sstatementFetchSize!=null) 
//			{
//				// nothing to do in ADO.NET
//			}
//		}

		public IClassMetadata GetClassMetadata(System.Type persistentClass) 
		{
			return GetPersister(persistentClass).ClassMetadata;
		}

		public ICollectionMetadata GetCollectionMetadata(string roleName) 
		{
			return (ICollectionMetadata) GetCollectionPersister(roleName);
		}

		public string[] GetImplementors(System.Type clazz) 
		{
			ArrayList results = new ArrayList();
			foreach(IClassPersister p in classPersisters.Values) 
			{
				if ( p is IQueryable ) 
				{
					IQueryable q = (IQueryable) p;
					string name = q.ClassName;
					bool isMappedClass = clazz.Equals( q.MappedClass );
					if ( q.IsExplicitPolymorphism ) 
					{
						if (isMappedClass) return new string[] { name };
					} 
					else 
					{
						if ( isMappedClass ) 
						{
							results.Add(name);
						} 
						else if (
							clazz.IsAssignableFrom( q.MappedClass ) &&
							( !q.IsInherited || !clazz.IsAssignableFrom( q.MappedSuperclass ) ) ) 
						{

							results.Add(name);
						}
					}
				}
			}
			return (string[]) results.ToArray(typeof(string));
		}

		
		public string GetImportedClassName(string name) 
		{
			string result = imports[name] as string;
			return (result==null) ? name : result;
		}

		public IDictionary GetAllClassMetadata() 
		{
			return classPersisters;
		}

		public IDictionary GetAllCollectionMetadata() 
		{
			 return collectionPersisters;
		}

		public void Close() 
		{
			log.Info("Closing");

			foreach(IClassPersister p in classPersisters.Values)
			{
				if ( p.HasCache ) p.Cache.Destroy();
			}
		
			foreach(CollectionPersister p in collectionPersisters.Values)
			{
				if ( p.HasCache ) p.CacheConcurrencyStrategy.Destroy();
			}

			try 
			{
				ConnectionProvider.Close();
			}
			finally 
			{
				SessionFactoryObjectFactory.RemoveInstance(uuid, name, properties);
			} 
		}

		public void Evict(System.Type persistentClass, object id) 
		{
			IClassPersister p = GetPersister(persistentClass);
			if(p.HasCache) p.Cache.Remove(id);
		}

		public void Evict(System.Type persistentClass) 
		{
			IClassPersister p = GetPersister(persistentClass);
			if(p.HasCache) p.Cache.Clear();
		}

		public void EvictCollection(string roleName, object id) 
		{
			CollectionPersister p = GetCollectionPersister(roleName);
			if(p.HasCache) p.CacheConcurrencyStrategy.Remove(id);
		}

		public void EvictCollection(string roleName) 
		{
			CollectionPersister p = GetCollectionPersister(roleName);
			if(p.HasCache) p.CacheConcurrencyStrategy.Clear();
		}
		
	}
}
