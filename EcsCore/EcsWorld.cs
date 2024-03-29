﻿using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using EcsCore.Components;
using EcsCore.Systems;

namespace EcsCore
{
    public class EcsWorld
    {
        public const int ROOT_ENTITY = 0;
        public const int NO_ENTITY = -1;

        public readonly int EntityCapacity;
        public int EntityCount { get; private set; }
        public bool IsAtEntityCapacity
        {
            get { return EntityCount == EntityCapacity; }
        }

        private int[] entities; // Entity ids. Dynamic loose hierarchy order.
        private int[] indices; // Indices of each entity to avoid linear searches. Fixed entity index order.
        private int[] parents; // Parents of each entity. Fixed entity index order.
        private IDictionary<Type, IDictionary<int, IEcsComponent>> components; // Component types for each entity. TODO: Dictionary of fixed size arrays of components, where components are structs rather than classes and never null, just reused.
        private ICollection<IEcsSystem> updateSystems;
        private ICollection<IEcsSystem> drawSystems;

        public delegate void ComponentEventHandler(int entity, IEcsComponent component);
        public event ComponentEventHandler OnComponentAdded;
        public event ComponentEventHandler OnComponentRemoved;

        public delegate void EntityEventHandler(int entity);
        public event EntityEventHandler OnEntityCreated;
        public event EntityEventHandler OnEntityDestroyed;

        public EcsWorld(int entityCapacity)
        {
            EntityCapacity = entityCapacity;
            EntityCount = 0;

            entities = new int[entityCapacity];
            indices = new int[entityCapacity];
            parents = new int[entityCapacity];
            for (int i = 0; i < entityCapacity; ++i)
            {
                entities[i] = i;
                indices[i] = i;
                parents[i] = ROOT_ENTITY;
            }
            components = new Dictionary<Type, IDictionary<int, IEcsComponent>>();
            updateSystems = new List<IEcsSystem>();
            drawSystems = new List<IEcsSystem>();

            // Start with transforms registered as the first draw system, to update poses.
            RegisterComponentType<Transform>();
            RegisterDrawSystem(new TransformSystem(this));

            // Create the root entity, not parented to anything.
            CreateEntity(NO_ENTITY);
        }

        public int CreateEntity(float x, float y, float rotation = 0f, int parentEntity = ROOT_ENTITY)
        {
            if (!IsAtEntityCapacity)
            {
                int entity = entities[EntityCount++];

                // All entities should have a transform by default. Anything that doesn't need a transform is better suited to being a separate process or system.
                AddComponent(entity, new Transform(x, y, rotation));

                Parent(entity, parentEntity);

                Console.WriteLine("Created " + entity);

                OnEntityCreated?.Invoke(entity);

                return entity;
            }
            else
            {
                Console.WriteLine("The number of entities is already at capacity " + EntityCount + " / " + EntityCapacity);
                return NO_ENTITY;
            }
        }

        public int CreateEntity(Vector2 position, float rotation = 0f, int parentEntity = ROOT_ENTITY)
        {
            return CreateEntity(position.X, position.Y, rotation, parentEntity);
        }

        public int CreateEntity(Transform transform, int parentEntity = ROOT_ENTITY)
        {
            return CreateEntity(transform.Position, transform.Rotation, parentEntity);
        }

        public int CreateEntity(int parentEntity = ROOT_ENTITY)
        {
            return CreateEntity(0, 0, 0, parentEntity);
        }

        public void DestroyEntity(int entity)
        {
            var index = GetEntityIndex(entity);
            if (index >= EntityCount)
            {
                throw new MissingEntityException("Entity " + entity + " has already been destroyed");
            }

            // Check for any children of this entity and destroy them first.
            for (var i = index + 1; i < EntityCount; ++i)
            {
                var otherEntity = entities[i];
                if (GetParent(otherEntity) == entity)
                {
                    DestroyEntity(otherEntity);
                }
            }

            // Remove all components at this entity.
            foreach (var componentsOfType in components)
            {
                if (componentsOfType.Value.ContainsKey(entity))
                {
                    componentsOfType.Value.Remove(entity);
                    Console.WriteLine("Removed " + componentsOfType.Key + " attached to " + entity);
                }
            }

            // Clear this entity's parent to the root.
            parents[entity] = ROOT_ENTITY;

            // Shift the entities after this entity back and place this entity directly after them.
            --EntityCount;
            for (var i = index; i < EntityCount; ++i)
            {
                SetEntityIndex(entities[i], i - 1);
            }
            SetEntityIndex(entity, EntityCount);

            Console.WriteLine("Destroyed " + entity);
            OnEntityDestroyed?.Invoke(entity);
        }

        public IEnumerable<int> GetEntities(IEnumerable<Type> requiredComponentTypes)
        {
            return entities.Where(entity => HasComponents(entity, requiredComponentTypes));
        }

        public IEnumerable<int> GetEntities(params Type[] requiredComponentTypes)
        {
            return GetEntities((IEnumerable<Type>)requiredComponentTypes);
        }

        public IEnumerable<int> GetEntities<TComponent>()
        {
            return GetEntities(typeof(TComponent));
        }

        private int GetEntityIndex(int entity)
        {
            if (entity >= ROOT_ENTITY)
            {
                return indices[entity];
            }
            else
            {
                return NO_ENTITY;
            }
        }

        private void SetEntityIndex(int entity, int index)
        {
            entities[index] = entity;
            indices[entity] = index;
        }

        public int GetParent(int entity)
        {
            return parents[entity];
        }

        public bool TryGetParent(int entity, out int parentEntity)
        {
            parentEntity = GetParent(entity);
            return parentEntity != NO_ENTITY;
        }

        public bool HasParent(int entity)
        {
            return parents[entity] != NO_ENTITY;
        }

        public void Parent(int entity, int parentEntity)
        {
            var index = GetEntityIndex(entity);
            if (index == NO_ENTITY || index >= EntityCount)
            {
                throw new MissingEntityException("Can't set the parent of the non-existent entity " + entity);
            }

            if (parentEntity >= ROOT_ENTITY)
            {
                var parentIndex = GetEntityIndex(parentEntity);
                if (parentIndex >= EntityCount)
                {
                    throw new MissingEntityException("Can't set the parent of entity " + entity + " to the non-existent entity " + parentEntity);
                }

                if (entity == parentEntity)
                {
                    throw new GrandfatherParadoxException("Can't set the parent of entity " + entity + " to itself.");
                }

                if (parents[entity] == parentEntity)
                {
                    return;
                }

                // Check that the parent is not a descendant of this entity.
                var grandparentEntity = GetParent(parentEntity);
                while (grandparentEntity > ROOT_ENTITY)
                {
                    if (grandparentEntity == entity)
                    {
                        throw new GrandfatherParadoxException("Can't set the parent of entity " + entity + " to entity" + parentEntity + " because it would cause a circular relationship.");
                    }
                    grandparentEntity = GetParent(grandparentEntity);
                }

                parents[entity] = parentEntity;

                // Make sure entities are in a valid order for the hierarchy.
                QuickSortEntities(Math.Min(index, parentIndex), EntityCount); // TODO: Something more efficient...
            }
            else
            {
                parents[entity] = NO_ENTITY;
            }
        }

        public void ParentToRoot(int entity)
        {
            Parent(entity, ROOT_ENTITY);
        }

        public void Unparent(int entity)
        {
            Parent(entity, NO_ENTITY);
        }

        private void QuickSortEntities(int leftIndex, int rightIndex)
        {
            if (rightIndex - leftIndex <= 1)
            {
                return;
            }

            var pivotIndex = PartitionEntities(leftIndex, rightIndex);

            QuickSortEntities(leftIndex, pivotIndex);
            QuickSortEntities(pivotIndex + 1, rightIndex);
        }

        private int PartitionEntities(int leftIndex, int rightIndex)
        {
            var lowerIndex = leftIndex;
            var higherIndex = rightIndex - 1;

            var pivot = entities[higherIndex];
            var pivotParentIndex = GetEntityIndex(parents[pivot]);

            while (higherIndex > lowerIndex)
            {
                var checkIndex = higherIndex - 1;
                var check = entities[checkIndex];
                if (GetEntityIndex(parents[check]) > pivotParentIndex)
                {
                    // Move the entity up and decrease the pivot index.
                    SetEntityIndex(check, higherIndex);
                    --higherIndex;
                }
                else
                {
                    // Swap the entity back with the next entity to check.
                    SetEntityIndex(entities[lowerIndex], checkIndex);
                    SetEntityIndex(check, lowerIndex);
                    ++lowerIndex;
                }
            }

            SetEntityIndex(pivot, higherIndex);

            return higherIndex;
        }

        public void RegisterComponentType<TComponent>() where TComponent : IEcsComponent
        {
            Type type = typeof(TComponent);
            if (!components.ContainsKey(type))
            {
                components.Add(typeof(TComponent), new Dictionary<int, IEcsComponent>());
                Console.WriteLine("Registered " + type);
            }
            else
            {
                throw new AlreadyRegisteredComponentException(type + " is already registered");
            }
        }

        public TComponent AddComponent<TComponent>(int entity, TComponent component) where TComponent : IEcsComponent
        {
            var componentsOfType = components[component.GetType()];
            if (!componentsOfType.ContainsKey(entity))
            {
                componentsOfType.Add(entity, component);
                Console.WriteLine("Added " + component.GetType() + " to " + entity);
                OnComponentAdded?.Invoke(entity, component);
                return component;
            }
            else
            {
                throw new ExistingComponentException(entity + " already has a " + component.GetType() + " component");
            }
        }

        public TComponent AddComponent<TComponent>(int entity) where TComponent : IEcsComponent, new()
        {
            return AddComponent(entity, new TComponent());
        }

        public TComponent RemoveComponent<TComponent>(int entity, TComponent component) where TComponent : IEcsComponent
        {
            Type type = component.GetType();
            var componentsOfType = components[type];
            IEcsComponent existingComponent;
            if (componentsOfType.TryGetValue(entity, out existingComponent))
            {
                if (existingComponent.Equals(component))
                {
                    componentsOfType.Remove(entity);
                    Console.WriteLine("Removed " + type + " from " + entity);
                    OnComponentRemoved?.Invoke(entity, component);
                    return component;
                }
                else
                {
                    throw new ExistingComponentException(entity + " has a different " + component.GetType() + " component");
                }
            }
            else
            {
                throw new MissingComponentException(entity + " does not have a " + component.GetType() + " component");
            }
        }

        public TComponent RemoveComponent<TComponent>(int entity) where TComponent : IEcsComponent
        {
            var type = typeof(TComponent);
            var componentsOfType = components[type];
            IEcsComponent component;
            if (componentsOfType.TryGetValue(entity, out component))
            {
                componentsOfType.Remove(entity);
                Console.WriteLine("Removed " + type + " from " + entity);
                OnComponentRemoved?.Invoke(entity, component);
                return (TComponent)component;
            }
            else
            {
                throw new MissingComponentException(entity + " does not have a " + typeof(TComponent) + " component");
            }
        }

        public bool HasComponent(int entity, Type componentType)
        {
            return components[componentType].ContainsKey(entity);
        }

        public bool HasComponent<TComponent>(int entity) where TComponent : IEcsComponent
        {
            return HasComponent(entity, typeof(TComponent));
        }

        public bool HasComponents(int entity, IEnumerable<Type> requiredComponentTypes)
        {
            return requiredComponentTypes.All(type => components[type].ContainsKey(entity));
        }

        public bool HasComponents(int entity, params Type[] requiredComponentTypes)
        {
            return HasComponents(entity, requiredComponentTypes);
        }

        public bool MatchesComponentFilter<TComponentFilter>(int entity)
        {
            var filterType = typeof(TComponentFilter);
            var fields = filterType.GetFields();
            return fields.All(field => HasComponent(entity, field.FieldType));
        }

        public IEnumerable<int> GetEntitiesMatchingComponentFilter<TComponentFilter>()
        {
            return entities.Where(entity => MatchesComponentFilter<TComponentFilter>(entity));
        }

        public TComponentFilter CreateComponentFilter<TComponentFilter>(int entity)
        {
            var filterType = typeof(TComponentFilter);
            var fields = filterType.GetFields();

            TComponentFilter filter = (TComponentFilter)Activator.CreateInstance(filterType);
            foreach (var field in fields)
            {
                field.SetValue(filter, GetComponent(entity, field.FieldType));
            }

            return filter;
        }

        public IEnumerable<TComponentFilter> CreateComponentFilters<TComponentFilter>(IEnumerable<int> entities)
        {
            return entities.Select(entity => CreateComponentFilter<TComponentFilter>(entity));
        }

        public IEcsComponent GetComponent(int entity, Type componentType)
        {
            return components[componentType][entity];
        }

        public TComponent GetComponent<TComponent>(int entity) where TComponent : IEcsComponent
        {
            return (TComponent)GetComponent(entity, typeof(TComponent));
        }

        public bool TryGetComponent(int entity, Type componentType, out IEcsComponent component)
        {
            return components[componentType].TryGetValue(entity, out component);
        }

        public bool TryGetComponent<TComponent>(int entity, out TComponent component) where TComponent : IEcsComponent
        {
            IEcsComponent genericComponent;
            bool success = TryGetComponent(entity, typeof(TComponent), out genericComponent);
            component = (TComponent)genericComponent;
            return success;
        }

        public IEnumerable<IEcsComponent> GetComponents(IEnumerable<int> entities, Type componentType)
        {
            var componentsOfType = components[componentType];
            return entities.Select(entity => componentsOfType[entity]);
        }

        public IEnumerable<TComponent> GetComponents<TComponent>(IEnumerable<int> entities) where TComponent : IEcsComponent
        {
            var componentsOfType = components[typeof(TComponent)];
            return entities.Select(entity => (TComponent)componentsOfType[entity]);
        }

        public void RegisterUpdateSystem(IEcsSystem system)
        {
            if (!updateSystems.Contains(system))
            {
                updateSystems.Add(system);
            }
            else
            {
                throw new AlreadyRegisteredSystemException("This " + system.GetType() + " is already registered");
            }
        }

        public void RegisterDrawSystem(IEcsSystem system)
        {
            if (!drawSystems.Contains(system))
            {
                drawSystems.Add(system);
            }
            else
            {
                throw new AlreadyRegisteredSystemException("This " + system.GetType() + " is already registered");
            }
        }

        public void Update(GameTime gameTime)
        {
            foreach (var updateSystem in updateSystems)
            {
                updateSystem.Process(gameTime);
            }
        }

        public void Draw(GameTime gameTime)
        {
            foreach (var drawSystem in drawSystems)
            {
                drawSystem.Process(gameTime);
            }
        }

        #region Exceptions
        public class MissingEntityException : Exception
        {
            public MissingEntityException(string message)
                : base(message)
            {
            }
        }

        public class GrandfatherParadoxException : Exception
        {
            public GrandfatherParadoxException(string message)
                : base(message)
            {
            }
        }

        public class MissingComponentException : Exception
        {
            public MissingComponentException(string message)
                : base(message)
            {
            }
        }

        public class ExistingComponentException : Exception
        {
            public ExistingComponentException(string message)
                : base(message)
            {
            }
        }

        public class AlreadyRegisteredComponentException : Exception
        {
            public AlreadyRegisteredComponentException(string message)
                : base(message)
            {
            }
        }

        public class AlreadyRegisteredSystemException : Exception
        {
            public AlreadyRegisteredSystemException(string message)
                : base(message)
            {
            }
        }
        #endregion
    }
}
