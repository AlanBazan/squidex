﻿// ==========================================================================
//  Squidex Headless CMS
// ==========================================================================
//  Copyright (c) Squidex UG (haftungsbeschränkt)
//  All rights reserved. Licensed under the MIT license.
// ==========================================================================

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Squidex.Domain.Apps.Core.Apps;
using Squidex.Domain.Apps.Entities.Apps.Commands;
using Squidex.Domain.Apps.Entities.Apps.Guards;
using Squidex.Domain.Apps.Entities.Apps.Services;
using Squidex.Domain.Apps.Entities.Apps.State;
using Squidex.Domain.Apps.Events;
using Squidex.Domain.Apps.Events.Apps;
using Squidex.Infrastructure;
using Squidex.Infrastructure.Commands;
using Squidex.Infrastructure.EventSourcing;
using Squidex.Infrastructure.Log;
using Squidex.Infrastructure.Orleans;
using Squidex.Infrastructure.Reflection;
using Squidex.Infrastructure.States;
using Squidex.Shared.Users;

namespace Squidex.Domain.Apps.Entities.Apps
{
    public sealed class AppGrain : SquidexDomainObjectGrain<AppState>, IAppGrain
    {
        private readonly InitialPatterns initialPatterns;
        private readonly IAppPlansProvider appPlansProvider;
        private readonly IAppPlanBillingManager appPlansBillingManager;
        private readonly IUserResolver userResolver;

        public AppGrain(
            InitialPatterns initialPatterns,
            IStore<Guid> store,
            ISemanticLog log,
            IAppPlansProvider appPlansProvider,
            IAppPlanBillingManager appPlansBillingManager,
            IUserResolver userResolver)
            : base(store, log)
        {
            Guard.NotNull(initialPatterns, nameof(initialPatterns));
            Guard.NotNull(userResolver, nameof(userResolver));
            Guard.NotNull(appPlansProvider, nameof(appPlansProvider));
            Guard.NotNull(appPlansBillingManager, nameof(appPlansBillingManager));

            this.userResolver = userResolver;
            this.appPlansProvider = appPlansProvider;
            this.appPlansBillingManager = appPlansBillingManager;
            this.initialPatterns = initialPatterns;
        }

        protected override Task<object> ExecuteAsync(IAggregateCommand command)
        {
            VerifyNotArchived();

            switch (command)
            {
                case CreateApp createApp:
                    return CreateAsync(createApp, c =>
                    {
                        GuardApp.CanCreate(c);

                        Create(c);
                    });

                case AssignContributor assignContributor:
                    return UpdateReturnAsync(assignContributor, async c =>
                    {
                        await GuardAppContributors.CanAssign(Snapshot.Contributors, Snapshot.Roles, c, userResolver, GetPlan());

                        AssignContributor(c, !Snapshot.Contributors.ContainsKey(assignContributor.ContributorId));

                        return EntityCreatedResult.Create(c.ContributorId, Version);
                    });

                case RemoveContributor removeContributor:
                    return UpdateAsync(removeContributor, c =>
                    {
                        GuardAppContributors.CanRemove(Snapshot.Contributors, c);

                        RemoveContributor(c);
                    });

                case AttachClient attachClient:
                    return UpdateAsync(attachClient, c =>
                    {
                        GuardAppClients.CanAttach(Snapshot.Clients, c);

                        AttachClient(c);
                    });

                case UpdateClient updateClient:
                    return UpdateAsync(updateClient, c =>
                    {
                        GuardAppClients.CanUpdate(Snapshot.Clients, c, Snapshot.Roles);

                        UpdateClient(c);
                    });

                case RevokeClient revokeClient:
                    return UpdateAsync(revokeClient, c =>
                    {
                        GuardAppClients.CanRevoke(Snapshot.Clients, c);

                        RevokeClient(c);
                    });

                case AddLanguage addLanguage:
                    return UpdateAsync(addLanguage, c =>
                    {
                        GuardAppLanguages.CanAdd(Snapshot.LanguagesConfig, c);

                        AddLanguage(c);
                    });

                case RemoveLanguage removeLanguage:
                    return UpdateAsync(removeLanguage, c =>
                    {
                        GuardAppLanguages.CanRemove(Snapshot.LanguagesConfig, c);

                        RemoveLanguage(c);
                    });

                case UpdateLanguage updateLanguage:
                    return UpdateAsync(updateLanguage, c =>
                    {
                        GuardAppLanguages.CanUpdate(Snapshot.LanguagesConfig, c);

                        UpdateLanguage(c);
                    });

                case AddRole addRole:
                    return UpdateAsync(addRole, c =>
                    {
                        GuardAppRoles.CanAdd(Snapshot.Roles, c);

                        AddRole(c);
                    });

                case DeleteRole deleteRole:
                    return UpdateAsync(deleteRole, c =>
                    {
                        GuardAppRoles.CanDelete(Snapshot.Roles, c, Snapshot.Contributors, Snapshot.Clients);

                        DeleteRole(c);
                    });

                case UpdateRole updateRole:
                    return UpdateAsync(updateRole, c =>
                    {
                        GuardAppRoles.CanUpdate(Snapshot.Roles, c);

                        UpdateRole(c);
                    });

                case AddPattern addPattern:
                    return UpdateAsync(addPattern, c =>
                    {
                        GuardAppPatterns.CanAdd(Snapshot.Patterns, c);

                        AddPattern(c);
                    });

                case DeletePattern deletePattern:
                    return UpdateAsync(deletePattern, c =>
                    {
                        GuardAppPatterns.CanDelete(Snapshot.Patterns, c);

                        DeletePattern(c);
                    });

                case UpdatePattern updatePattern:
                    return UpdateAsync(updatePattern, c =>
                    {
                        GuardAppPatterns.CanUpdate(Snapshot.Patterns, c);

                        UpdatePattern(c);
                    });

                case ChangePlan changePlan:
                    return UpdateReturnAsync(changePlan, async c =>
                    {
                        GuardApp.CanChangePlan(c, Snapshot.Plan, appPlansProvider);

                        if (c.FromCallback)
                        {
                            ChangePlan(c);

                            return null;
                        }
                        else
                        {
                            var result = await appPlansBillingManager.ChangePlanAsync(c.Actor.Identifier, Snapshot.Id, Snapshot.Name, c.PlanId);

                            if (result is PlanChangedResult)
                            {
                                if (result is PlanResetResult)
                                {
                                    ResetPlan(c);
                                }
                                else
                                {
                                    ChangePlan(c);
                                }
                            }

                            return result;
                        }
                    });

                case ArchiveApp archiveApp:
                    return UpdateAsync(archiveApp, async c =>
                    {
                        await appPlansBillingManager.ChangePlanAsync(c.Actor.Identifier, Snapshot.Id, Snapshot.Name, null);

                        ArchiveApp(c);
                    });

                default:
                    throw new NotSupportedException();
            }
        }

        private IAppLimitsPlan GetPlan()
        {
            return appPlansProvider.GetPlan(Snapshot.Plan?.PlanId);
        }

        public void Create(CreateApp command)
        {
            var appId = NamedId.Of(command.AppId, command.Name);

            var events = new List<AppEvent>
            {
                CreateInitalEvent(command.Name),
                CreateInitialOwner(command.Actor),
                CreateInitialLanguage()
            };

            foreach (var pattern in initialPatterns)
            {
                events.Add(CreateInitialPattern(pattern.Key, pattern.Value));
            }

            foreach (var @event in events)
            {
                @event.Actor = command.Actor;
                @event.AppId = appId;

                RaiseEvent(@event);
            }
        }

        public void UpdateClient(UpdateClient command)
        {
            if (!string.IsNullOrWhiteSpace(command.Name))
            {
                RaiseEvent(SimpleMapper.Map(command, new AppClientRenamed()));
            }

            if (command.Role != null)
            {
                RaiseEvent(SimpleMapper.Map(command, new AppClientUpdated { Role = command.Role }));
            }
        }

        public void UpdateLanguage(UpdateLanguage command)
        {
            RaiseEvent(SimpleMapper.Map(command, new AppLanguageUpdated()));
        }

        public void AssignContributor(AssignContributor command, bool isAdded)
        {
            RaiseEvent(SimpleMapper.Map(command, new AppContributorAssigned { IsAdded = isAdded }));
        }

        public void RemoveContributor(RemoveContributor command)
        {
            RaiseEvent(SimpleMapper.Map(command, new AppContributorRemoved()));
        }

        public void AttachClient(AttachClient command)
        {
            RaiseEvent(SimpleMapper.Map(command, new AppClientAttached()));
        }

        public void RevokeClient(RevokeClient command)
        {
            RaiseEvent(SimpleMapper.Map(command, new AppClientRevoked()));
        }

        public void AddLanguage(AddLanguage command)
        {
            RaiseEvent(SimpleMapper.Map(command, new AppLanguageAdded()));
        }

        public void RemoveLanguage(RemoveLanguage command)
        {
            RaiseEvent(SimpleMapper.Map(command, new AppLanguageRemoved()));
        }

        public void ChangePlan(ChangePlan command)
        {
            RaiseEvent(SimpleMapper.Map(command, new AppPlanChanged()));
        }

        public void ResetPlan(ChangePlan command)
        {
            RaiseEvent(SimpleMapper.Map(command, new AppPlanReset()));
        }

        public void AddPattern(AddPattern command)
        {
            RaiseEvent(SimpleMapper.Map(command, new AppPatternAdded()));
        }

        public void DeletePattern(DeletePattern command)
        {
            RaiseEvent(SimpleMapper.Map(command, new AppPatternDeleted()));
        }

        public void UpdatePattern(UpdatePattern command)
        {
            RaiseEvent(SimpleMapper.Map(command, new AppPatternUpdated()));
        }

        public void AddRole(AddRole command)
        {
            RaiseEvent(SimpleMapper.Map(command, new AppRoleAdded()));
        }

        public void DeleteRole(DeleteRole command)
        {
            RaiseEvent(SimpleMapper.Map(command, new AppRoleDeleted()));
        }

        public void UpdateRole(UpdateRole command)
        {
            RaiseEvent(SimpleMapper.Map(command, new AppRoleUpdated()));
        }

        public void ArchiveApp(ArchiveApp command)
        {
            RaiseEvent(SimpleMapper.Map(command, new AppArchived()));
        }

        private void VerifyNotArchived()
        {
            if (Snapshot.IsArchived)
            {
                throw new DomainException("App has already been archived.");
            }
        }

        private void RaiseEvent(AppEvent @event)
        {
            if (@event.AppId == null)
            {
                @event.AppId = NamedId.Of(Snapshot.Id, Snapshot.Name);
            }

            RaiseEvent(Envelope.Create(@event));
        }

        private static AppCreated CreateInitalEvent(string name)
        {
            return new AppCreated { Name = name };
        }

        private static AppPatternAdded CreateInitialPattern(Guid id, AppPattern pattern)
        {
            return new AppPatternAdded { PatternId = id, Name = pattern.Name, Pattern = pattern.Pattern, Message = pattern.Message };
        }

        private static AppLanguageAdded CreateInitialLanguage()
        {
            return new AppLanguageAdded { Language = Language.EN };
        }

        private static AppContributorAssigned CreateInitialOwner(RefToken actor)
        {
            return new AppContributorAssigned { ContributorId = actor.Identifier, Role = Role.Owner };
        }

        public Task<J<IAppEntity>> GetStateAsync()
        {
            return J.AsTask<IAppEntity>(Snapshot);
        }
    }
}
