﻿using Hake.Extension.StateMachine.Internal;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace Hake.Extension.StateMachine
{

    public sealed class StateMachine<TState, TInput> : IStateMachine<TState, TInput>
    {
        private static object DynamicInvokeMethod(object obj, string name, params object[] param)
        {
            return obj.GetType().GetTypeInfo().GetDeclaredMethod(name).Invoke(obj, param);
        }
        private static object DynamicInvokeMethod(TypeInfo type, object obj, string name, params object[] param)
        {
            return type.GetDeclaredMethod(name).Invoke(obj, param);
        }
        private static object DynamicGetProperty(object obj, string name)
        {
            return obj.GetType().GetTypeInfo().GetDeclaredProperty(name).GetMethod.Invoke(obj, null);
        }
        private static object DynamicGetProperty(TypeInfo type, object obj, string name)
        {
            return type.GetDeclaredProperty(name).GetMethod.Invoke(obj, null);
        }

        private TState state;
        public TState State => state;

        private IDictionary<TState, TransformationRecord<TState, TInput>> transformationTable;
        private IDictionary<TState, TransformationBuilder<TState, TInput>> transformationBuilders;
        private IList<Action<IStateMachine<TState, TInput>, TriggerType>> startingActions;
        private IList<Action<StateMachineEndingContext<TState, TInput>>> endingActions;
        private bool configurationLocked;

        public StateMachine()
        {
            state = default(TState);
            transformationBuilders = new Dictionary<TState, TransformationBuilder<TState, TInput>>();
            startingActions = new List<Action<IStateMachine<TState, TInput>, TriggerType>>();
            endingActions = new List<Action<StateMachineEndingContext<TState, TInput>>>();
            configurationLocked = false;
        }

        public ITransformationBuilder<TState, TInput> Configure(TState state)
        {
            TransformationBuilder<TState, TInput> builder;
            if (transformationBuilders.TryGetValue(state, out builder))
                return builder;
            builder = new TransformationBuilder<TState, TInput>(this, state);
            transformationBuilders[state] = builder;
            configurationLocked = false;
            return builder;
        }

        public InvokeResult<TState> Invoke(TState initialState, IEnumerable<TInput> inputs)
        {
            if (!configurationLocked)
                Rebuild();
            state = initialState;
            foreach (Action<IStateMachine<TState, TInput>, TriggerType> action in startingActions)
                action(this, TriggerType.Main);

            StateMachineEndingContext<TState, TInput> context;
            bool invokeEnding = true;
            int position = 0;
            InvokeStateMachine(inputs, position);
            if (invokeEnding)
            {
                do
                {
                    context = new StateMachineEndingContext<TState, TInput>(this, EndingReason.NoMoreInput, TriggerType.Main);
                    foreach (Action<StateMachineEndingContext<TState, TInput>> action in endingActions)
                        action.Invoke(context);
                    if (context.Handled && context.Action == StateMachineEndingAction.ContinueWithFeededInputs)
                        InvokeStateMachine(context.FeededInputs, 0);

                    else
                        break;
                } while (true);
            }
            return new InvokeResult<TState>(0, initialState, position, state);

            void InvokeStateMachine(IEnumerable<TInput> elements, int begin)
            {
                IEnumerator<TInput> enumerator = elements.GetEnumerator();
                while (begin > 0)
                {
                    enumerator.MoveNext();
                    begin--;
                }
                TInput input;
                while (enumerator.MoveNext())
                {
                    input = enumerator.Current;
                    if (transformationTable.TryGetValue(state, out TransformationRecord<TState, TInput> record))
                    {
                        if (record.Transform(position, input, elements, TriggerType.Process, out TState newState, out FollowingAction followingAction, out object shiftContext, out object stateMapper, out object callback, out object callbackData))
                        {
                            state = newState;
                            position++;
                            if (followingAction == FollowingAction.Stop)
                            {
                                context = new StateMachineEndingContext<TState, TInput>(this, EndingReason.EarlyStopped, TriggerType.Process);
                                foreach (Action<StateMachineEndingContext<TState, TInput>> action in endingActions)
                                    action.Invoke(context);
                                invokeEnding = false;
                                break;
                            }
                            else if (followingAction == FollowingAction.Shift)
                            {
                                TState oldState = state;
                                object result = null;
                                Exception exception = null;
                                TypeInfo shiftContextType = shiftContext.GetType().GetTypeInfo();
                                object processor = DynamicGetProperty(shiftContextType, shiftContext, "Processor");
                                try
                                {
                                    result = DynamicInvokeMethod(shiftContextType, shiftContext, "Invoke");
                                }
                                catch (Exception ex)
                                {
                                    exception = ex;
                                }
                                TypeInfo resultType = result.GetType().GetTypeInfo();
                                object substate = DynamicGetProperty(resultType, result, "EndState");
                                object newstate = substate;
                                int endposition = (int)DynamicGetProperty(resultType, result, "EndPosition");
                                int startposition = (int)DynamicGetProperty(resultType, result, "StartPosition");
                                if (stateMapper != null)
                                {
                                    newstate = ((Delegate)stateMapper).DynamicInvoke(newstate);
                                }
                                while (position < endposition)
                                {
                                    enumerator.MoveNext();
                                    position++;
                                }
                                state = (TState)newstate;
                                if (callback != null)
                                {
                                    TypeInfo objectType = callback.GetType().GenericTypeArguments[0].GetTypeInfo();

                                    object[] parameter = new object[] { callbackData, exception, processor, substate, oldState, stateMapper, startposition, endposition };
                                    object shiftResult = objectType.GetDeclaredMethod("Create").Invoke(null, parameter);
                                    ((Delegate)callback).DynamicInvoke(shiftResult);
                                }
                            }
                        }
                        else
                            throw new Exception($"no transform information given.\r\ncurrent state: {state}\r\ninput: {input}");
                    }
                    else
                        throw new Exception($"no transform information given.\r\ncurrent state: {state}");
                }
            }
        }


        public TState InvokeOneShot(TState initialState, TInput input)
        {
            if (!configurationLocked)
                Rebuild();

            state = initialState;
            foreach (Action<IStateMachine<TState, TInput>, TriggerType> action in startingActions)
                action(this, TriggerType.OneShot);
            return InvokeOneShot(input);
        }

        public TState InvokeOneShot(TInput input)
        {
            if (!configurationLocked)
                throw new Exception("initial state must be provided");

            TransformationRecord<TState, TInput> record;
            TState newState;
            FollowingAction followingAction;
            if (transformationTable.TryGetValue(state, out record))
            {
                if (record.Transform(0, input, new TInput[1] { input }, TriggerType.OneShot, out newState, out followingAction, out object shiftContext, out object stateMapper, out object callback, out object callbackData))
                    state = newState;
                else
                    throw new Exception($"no transform information given.\r\ncurrent state: {state}\r\ninput: {input}");
            }
            else
                throw new Exception($"no transform information given.\r\ncurrent state: {state}");
            return state;
        }

        public IStateMachine<TState, TInput> OnEnding(Action<StateMachineEndingContext<TState, TInput>> context)
        {
            if (context != null)
                endingActions.Add(context);
            return this;
        }

        public IStateMachine<TState, TInput> OnStarting(Action<IStateMachine<TState, TInput>, TriggerType> action)
        {
            if (action != null)
                startingActions.Add(action);
            return this;
        }

        private void Rebuild()
        {
            transformationTable = new Dictionary<TState, TransformationRecord<TState, TInput>>();
            foreach (var pair in transformationBuilders)
                transformationTable[pair.Key] = pair.Value.Build();
            configurationLocked = true;
        }

        public InvokeResult<TState> InvokeProcess(TState initialState, IEnumerable<TInput> inputs, int position)
        {
            if (!configurationLocked)
                Rebuild();
            int startPosition = position;
            state = initialState;
            foreach (Action<IStateMachine<TState, TInput>, TriggerType> action in startingActions)
                action(this, TriggerType.Process);

            StateMachineEndingContext<TState, TInput> context;
            bool invokeEnding = true;
            InvokeStateMachine(inputs, position);
            if (invokeEnding)
            {
                do
                {
                    context = new StateMachineEndingContext<TState, TInput>(this, EndingReason.NoMoreInput, TriggerType.Process);
                    foreach (Action<StateMachineEndingContext<TState, TInput>> action in endingActions)
                        action.Invoke(context);
                    if (context.Handled && context.Action == StateMachineEndingAction.ContinueWithFeededInputs)
                        InvokeStateMachine(context.FeededInputs, 0);

                    else
                        break;
                } while (true);
            }
            return new InvokeResult<TState>(startPosition, initialState, position, state);

            void InvokeStateMachine(IEnumerable<TInput> elements, int begin)
            {
                IEnumerator<TInput> enumerator = elements.GetEnumerator();
                while (begin > 0)
                {
                    enumerator.MoveNext();
                    begin--;
                }
                TInput input;
                while (enumerator.MoveNext())
                {
                    input = enumerator.Current;
                    if (transformationTable.TryGetValue(state, out TransformationRecord<TState, TInput> record))
                    {
                        if (record.Transform(position, input, elements, TriggerType.Process, out TState newState, out FollowingAction followingAction, out object shiftContext, out object stateMapper, out object callback, out object callbackData))
                        {
                            state = newState;
                            position++;
                            if (followingAction == FollowingAction.Stop)
                            {
                                context = new StateMachineEndingContext<TState, TInput>(this, EndingReason.EarlyStopped, TriggerType.Process);
                                foreach (Action<StateMachineEndingContext<TState, TInput>> action in endingActions)
                                    action.Invoke(context);
                                invokeEnding = false;
                                break;
                            }
                            else if (followingAction == FollowingAction.Shift)
                            {
                                TState oldState = state;
                                object result = null;
                                Exception exception = null;
                                TypeInfo shiftContextType = shiftContext.GetType().GetTypeInfo();
                                object processor = DynamicGetProperty(shiftContextType, shiftContext, "Processor");
                                try
                                {
                                    result = DynamicInvokeMethod(shiftContextType, shiftContext, "Invoke");
                                }
                                catch (Exception ex)
                                {
                                    exception = ex;
                                }
                                TypeInfo resultType = result.GetType().GetTypeInfo();
                                object substate = DynamicGetProperty(resultType, result, "EndState");
                                object newstate = substate;
                                int endposition = (int)DynamicGetProperty(resultType, result, "EndPosition");
                                int startposition = (int)DynamicGetProperty(resultType, result, "StartPosition");
                                if (stateMapper != null)
                                {
                                    newstate = ((Delegate)stateMapper).DynamicInvoke(newstate);
                                }
                                while (position < endposition)
                                {
                                    enumerator.MoveNext();
                                    position++;
                                }
                                state = (TState)newstate;
                                if (callback != null)
                                {
                                    TypeInfo objectType = callback.GetType().GenericTypeArguments[0].GetTypeInfo();

                                    object[] parameter = new object[] { callbackData, exception, processor, substate, oldState, stateMapper, startposition, endposition };
                                    object shiftResult = objectType.GetDeclaredMethod("Create").Invoke(null, parameter);
                                    ((Delegate)callback).DynamicInvoke(shiftResult);
                                }
                            }
                        }
                        else
                            throw new Exception($"no transform information given.\r\ncurrent state: {state}\r\ninput: {input}");
                    }
                    else
                        throw new Exception($"no transform information given.\r\ncurrent state: {state}");
                }
            }
        }
    }

}
