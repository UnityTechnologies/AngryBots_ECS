using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reflection;

namespace Unity.Entities
{
#if !UNITY_ZEROPLAYER
    public partial class World : IDisposable
    {
        public static World Active { get; set; }

        static readonly List<World> allWorlds = new List<World>();

        public static ReadOnlyCollection<World> AllWorlds => new ReadOnlyCollection<World>(allWorlds);

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        bool m_AllowGetSystem = true;
#endif

        //@TODO: What about multiple managers of the same type...
        Dictionary<Type, ComponentSystemBase> m_SystemLookup =
            new Dictionary<Type, ComponentSystemBase>();

        List<ComponentSystemBase> m_Systems = new List<ComponentSystemBase>();
        private EntityManager m_EntityManager;

        static int ms_SystemIDAllocator = 0;

        public IEnumerable<ComponentSystemBase> Systems =>
            new ReadOnlyCollection<ComponentSystemBase>(m_Systems);

        public string Name { get; }

        public override string ToString()
        {
            return Name;
        }

        public int Version { get; private set; }

        public EntityManager EntityManager => m_EntityManager;

        public bool IsCreated => m_Systems != null;

        public World(string name)
        {
            // Debug.LogError("Create World "+ name + " - " + GetHashCode());
            Name = name;
            allWorlds.Add(this);

            m_EntityManager = new EntityManager(this);
        }

        public void Dispose()
        {
            if (!IsCreated)
                throw new ArgumentException("The World has already been Disposed.");
            // Debug.LogError("Dispose World "+ Name + " - " + GetHashCode());

            if (allWorlds.Contains(this))
                allWorlds.Remove(this);

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            m_AllowGetSystem = false;
#endif
            // Destruction should happen in reverse order to construction
            for (int i = m_Systems.Count - 1; i >= 0; --i)
            {
                try
                {
                    m_Systems[i].DestroyInstance();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }

            // Destroy EntityManager last
            m_EntityManager.DestroyInstance();
            m_EntityManager = null;

            m_Systems.Clear();
            m_SystemLookup.Clear();

            m_Systems = null;
            m_SystemLookup = null;

            if (Active == this)
                Active = null;
        }

        public static void DisposeAllWorlds()
        {
            while (allWorlds.Count != 0)
                allWorlds[0].Dispose();
        }

        ComponentSystemBase CreateSystemInternal(Type type, object[] constructorArguments)
        {
            if (!typeof(ComponentSystemBase).IsAssignableFrom(type))
            {
                throw new ArgumentException($"Type {type} must be derived from ComponentSystem or JobComponentSystem.");
            }

#if ENABLE_UNITY_COLLECTIONS_CHECKS

            if (constructorArguments != null && constructorArguments.Length != 0)
            {
                var constructors =
                    type.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (constructors.Length == 1 && constructors[0].IsPrivate)
                    throw new MissingMethodException(
                        $"Constructing {type} failed because the constructor was private, it must be public.");
            }

            m_AllowGetSystem = false;
#endif
            ComponentSystemBase system;
            try
            {
                system = Activator.CreateInstance(type, constructorArguments) as ComponentSystemBase;
            }
            catch (MissingMethodException)
            {
                Debug.LogError($"[Job]ComponentSystem {type} must be mentioned in a link.xml file, or annotated " +
                                "with a [Preserve] attribute to prevent its constructor from being stripped.  " +
                                "See https://docs.unity3d.com/Manual/ManagedCodeStripping.html for more information.");
                throw;
            }
            finally
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                m_AllowGetSystem = true;
#endif
            }

            return AddSystem(system);
        }

        ComponentSystemBase GetExistingSystemInternal(Type type)
        {
            ComponentSystemBase manager;
            if (m_SystemLookup.TryGetValue(type, out manager))
                return manager;

            return null;
        }

        ComponentSystemBase GetOrCreateSystemInternal(Type type)
        {
            var manager = GetExistingSystemInternal(type);

            return manager ?? CreateSystemInternal(type, null);
        }

        void AddTypeLookup(Type type, ComponentSystemBase manager)
        {
            while (type != typeof(ComponentSystemBase))
            {
                if (!m_SystemLookup.ContainsKey(type))
                    m_SystemLookup.Add(type, manager);

                type = type.BaseType;
            }
        }

        void RemoveSystemInternal(ComponentSystemBase manager)
        {
            if (!m_Systems.Remove(manager))
                throw new ArgumentException($"manager does not exist in the world");
            ++Version;

            var type = manager.GetType();
            while (type != typeof(ComponentSystemBase))
            {
                if (m_SystemLookup[type] == manager)
                {
                    m_SystemLookup.Remove(type);

                    foreach (var otherManager in m_Systems)
                        if (otherManager.GetType().IsSubclassOf(type))
                            AddTypeLookup(otherManager.GetType(), otherManager);
                }

                type = type.BaseType;
            }
        }

        void CheckGetOrCreateSystem()
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (!IsCreated)
                throw new ArgumentException("The World has already been Disposed.");
            if (!m_AllowGetSystem)
                throw new ArgumentException(
                    "You are not allowed to get or create more systems during destruction and constructor of a system.");
#endif
        }

        void CheckCreated()
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (!IsCreated)
                throw new ArgumentException("The World has already been Disposed.");
#endif
        }

        public ComponentSystemBase CreateSystem(Type type, params object[] constructorArgumnents)
        {
            CheckGetOrCreateSystem();

            return CreateSystemInternal(type, constructorArgumnents);
        }

        public T CreateSystem<T>(params object[] constructorArgumnents) where T : ComponentSystemBase
        {
            CheckGetOrCreateSystem();

            return (T) CreateSystemInternal(typeof(T), constructorArgumnents);
        }

        public T GetOrCreateSystem<T>() where T : ComponentSystemBase
        {
            CheckGetOrCreateSystem();

            return (T) GetOrCreateSystemInternal(typeof(T));
        }

        public ComponentSystemBase GetOrCreateSystem(Type type)
        {
            CheckGetOrCreateSystem();

            return GetOrCreateSystemInternal(type);
        }

        public T AddSystem<T>(T system) where T : ComponentSystemBase
        {
            CheckGetOrCreateSystem();

            m_Systems.Add(system);
            AddTypeLookup(system.GetType(), system);

            try
            {
                system.CreateInstance(this);
            }
            catch
            {
                RemoveSystemInternal(system);
                throw;
            }
            ++Version;
            return system;
        }

        public T GetExistingSystem<T>() where T : ComponentSystemBase
        {
            CheckGetOrCreateSystem();

            return (T) GetExistingSystemInternal(typeof(T));
        }

        public ComponentSystemBase GetExistingSystem(Type type)
        {
            CheckGetOrCreateSystem();

            return GetExistingSystemInternal(type);
        }

        public void DestroySystem(ComponentSystemBase system)
        {
            CheckGetOrCreateSystem();

            RemoveSystemInternal(system);
            system.DestroyInstance();
        }

        public bool QuitUpdate { get; set; }

        internal static int AllocateSystemID()
        {
            return ++ms_SystemIDAllocator;
        }
    }
#else
    public partial class World : IDisposable
    {
        public static World Active { get; set; }

        static readonly List<World> allWorlds = new List<World>();

        public static World[] AllWorlds => allWorlds.ToArray();

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        bool m_AllowGetSystem = true;
#endif

        //@TODO: What about multiple managers of the same type...
        List<ComponentSystemBase> m_Systems = new List<ComponentSystemBase>();
        private EntityManager m_EntityManager;

        int m_SystemIDAllocator = 0;

        public ComponentSystemBase[] Systems => m_Systems.ToArray();

        public string Name { get; }

        public override string ToString()
        {
            return Name;
        }

        public int Version { get; private set; }

        public EntityManager EntityManager => m_EntityManager;

        public bool IsCreated => true;

        public World(string name)
        {
            // Debug.LogError("Create World "+ name + " - " + GetHashCode());
            Name = name;
            allWorlds.Add(this);

            m_EntityManager = new EntityManager(this);
            Version++;
        }

        public void Dispose()
        {
            if (!IsCreated)
                throw new ArgumentException("World is already disposed");
            // Debug.LogError("Dispose World "+ Name + " - " + GetHashCode());

            if (allWorlds.Contains(this))
                allWorlds.Remove(this);

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            m_AllowGetSystem = false;
#endif

            // Destruction should happen in reverse order to construction
            for (int i = m_Systems.Count - 1; i >= 0; --i)
            {
                try
                {
                    m_Systems[i].DestroyInstance();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }

            // Destroy EntityManager last
            m_EntityManager.DestroyInstance();
            m_EntityManager = null;

            m_Systems.Clear();
            m_Systems = null;

            if (Active == this)
                Active = null;
        }

        public static void DisposeAllWorlds()
        {
            while (allWorlds.Count != 0)
                allWorlds[0].Dispose();
        }

        private ComponentSystemBase CreateSystemInternal<T>() where T : new()
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (!m_AllowGetSystem)
                throw new ArgumentException(
                    "During destruction of a system you are not allowed to create more systems.");

            m_AllowGetSystem = true;
#endif
            ComponentSystemBase system;
            try
            {
#if !NET_DOTS
                system = new T() as ComponentSystemBase;
#else
                system = TypeManager.ConstructSystem(typeof(T));
#endif
            }
            catch
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                m_AllowGetSystem = false;
#endif
                throw;
            }

            return AddSystem(system);
        }

        private ComponentSystemBase GetExistingSystemInternal<T>()
        {
            return GetExistingSystem(typeof(T));
        }

        private ComponentSystemBase GetExistingSystemInternal(Type type)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (!IsCreated)
                throw new ArgumentException("During destruction ");
            if (!m_AllowGetSystem)
                throw new ArgumentException(
                    "During destruction of a system you are not allowed to get or create more systems.");
#endif

            for (int i = 0; i < m_Systems.Count; ++i) {
                var mgr = m_Systems[i];
                if (type.IsAssignableFrom(mgr.GetType()))
                    return mgr;
            }

            return null;
        }

        private ComponentSystemBase GetOrCreateSystemInternal<T>() where T : new()
        {
            var manager = GetExistingSystemInternal<T>();
            return manager ?? CreateSystemInternal<T>();
        }

        private void RemoveSystemInternal(ComponentSystemBase system)
        {
            if (!m_Systems.Remove(system))
                throw new ArgumentException($"manager does not exist in the world");
            ++Version;
        }

        public T CreateSystem<T>() where T : ComponentSystemBase, new()
        {
            return (T) CreateSystemInternal<T>();
        }

        public T GetOrCreateSystem<T>() where T : ComponentSystemBase, new()
        {
            return (T) GetOrCreateSystemInternal<T>();
        }

        public T AddSystem<T>(T system) where T : ComponentSystemBase
        {
            m_Systems.Add(system);
            try
            {
                system.CreateInstance(this);
            }
            catch
            {
                RemoveSystemInternal(system);
                throw;
            }

            ++Version;
            return system;
        }

        public T GetExistingSystem<T>() where T : ComponentSystemBase
        {
            return (T) GetExistingSystemInternal(typeof(T));
        }

        public ComponentSystemBase GetExistingSystem(Type type)
        {
            return GetExistingSystemInternal(type);
        }

        public void DestroySystem(ComponentSystemBase system)
        {
            RemoveSystemInternal(system);
            system.DestroyInstance();
        }

        static int ms_SystemIDAllocator = 0;
        internal static int AllocateSystemID()
        {
            return ++ms_SystemIDAllocator;
        }

        public bool QuitUpdate { get; set; }

        public void Update()
        {
            InitializationSystemGroup initializationSystemGroup =
                GetExistingSystem(typeof(InitializationSystemGroup)) as InitializationSystemGroup;
            SimulationSystemGroup simulationSystemGroup =
                GetExistingSystem(typeof(SimulationSystemGroup)) as SimulationSystemGroup;
            PresentationSystemGroup presentationSystemGroup =
                GetExistingSystem(typeof(PresentationSystemGroup)) as PresentationSystemGroup;

            initializationSystemGroup?.Update();
            simulationSystemGroup?.Update();
            presentationSystemGroup?.Update();
        }
    }
#endif
}
