using System;
using System.Linq;
using Microsoft.Practices.ServiceLocation;
using ScopedUnitOfWork.Interfaces;
using ScopedUnitOfWork.Interfaces.Exceptions;

namespace ScopedUnitOfWork.Framework
{
    public abstract class UnitOfWorkScopeManagerBase<TContext> : IScopeManager where TContext : class, IDisposable
    {
        protected readonly IServiceLocator ServiceLocator;

        /// <summary>
        /// This little thing here with ThreadStatic ensure that contexts are thred-safe since
        /// they will never be shared. Due to this being on generic type, it also ensures
        /// that each context type gets its own variable, making multiple stack for different 
        /// context types possible on single thread.
        /// </summary>
        [ThreadStatic]
        private static UoWScopeStack<TContext> _scopeStack;

        protected UnitOfWorkScopeManagerBase(IServiceLocator serviceLocator)
        {
            if (serviceLocator == null) throw new ArgumentNullException(nameof(serviceLocator));

            ServiceLocator = serviceLocator;
        }

        public IUnitOfWork CreateNew(ScopeType scopeType)
        {
            ThrowIfUnknownScopeType(scopeType);

            // in case there were no units of work before or the ones that existed completely ended and self-destructed
            CreateNewStackIfNoneExist();

            var newUnitOfWork = CreateUnitOfWork(scopeType);
            ScopeStack.Stack.Push(newUnitOfWork);

            if (scopeType == ScopeType.Transactional && !ScopeStack.HasTransaction())
            {
                ScopeStack.SetTransaction(CreateAndStartTransaction());
            }

            return newUnitOfWork;
        }

        protected abstract IUnitOfWork CreateUnitOfWork(ScopeType scopeType);
        protected abstract ITransactionWrapper CreateAndStartTransaction();

        public void Complete(IUnitOfWork unitOfWork)
        {
            ThrowIfNotLastScoped(unitOfWork);

            if (unitOfWork.ScopeType == ScopeType.Transactional)
            {
                if (_scopeStack.IsRolledBack())
                    throw new TransactionFailedException("Could not complete this transaction since an " +
                                                         "inner transactional unit of work did not succeed correctly.");

                // commit only if topmost
                if (!_scopeStack.AnyTransactionalUnitsOfWorkBesides(unitOfWork))
                {
                    _scopeStack.Transaction.Commit();
                }
            }
        }

        public void Remove(IUnitOfWork unitOfWork)
        {
            // simply ignore if not in the stack at all
            if (_scopeStack == null || !_scopeStack.Stack.Contains(unitOfWork))
                return;

            ThrowIfNotLastScoped(unitOfWork);

            _scopeStack.Stack.Pop();

            if (unitOfWork.ScopeType == ScopeType.Transactional)
            {
                // we check if not already commited / rolled-back
                if (!unitOfWork.IsFinished && !_scopeStack.IsRolledBack())
                {
                    _scopeStack.RollBack();
                }

                // if very last transaction, we can remove the transactional portion of the stack
                if (!_scopeStack.AnyTransactionalUnitsOfWork())
                    _scopeStack.CleanTransaction();
            }

            // if last, remove the stack / context
            if (!_scopeStack.Stack.Any())
            {
                _scopeStack.Dispose();
                _scopeStack = null;
            }
        }

        public static UoWScopeStack<TContext> ScopeStack
        {
            get { return _scopeStack; }
            private set { _scopeStack = value; }
        }

        private void CreateNewStackIfNoneExist()
        {
            if (ScopeStack == null)
            {
                ScopeStack = new UoWScopeStack<TContext>(ServiceLocator.GetInstance<TContext>());
            }
        }

        private static void ThrowIfUnknownScopeType(ScopeType scopeType)
        {
            if (scopeType != ScopeType.Default && scopeType != ScopeType.Transactional)
                throw new ArgumentException("Unknown scope type provided: " + scopeType +
                                            ". Could not create a new Unit of Work");
        }

        private static void ThrowIfNotLastScoped(IUnitOfWork unitOfWork)
        {
            if (_scopeStack == null || !ReferenceEquals(_scopeStack.Stack.Peek(), unitOfWork))
            {
                throw new IncorrectUnitOfWorkUsageException(
                    "Trying to remove a unit of work resulted in detection of incorrect " +
                    "unit of work pattern usage. This implementation must be always using by " +
                    "using the C# 'using' statement. Do not manually try to dispose UoW by hand, " +
                    "but if you do make sure they are always disposed in reverse order of usage (LIFO).");
            }
        }
    }
}