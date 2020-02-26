﻿// ==========================================================================
//  Squidex Headless CMS
// ==========================================================================
//  Copyright (c) Squidex UG (haftungsbeschraenkt)
//  All rights reserved. Licensed under the MIT license.
// ==========================================================================

using Squidex.Domain.Apps.Events.Contents;
using Squidex.Infrastructure.Reflection;

namespace Migrate_01.OldEvents
{
    [TypeName(nameof(NoopConventEvent))]
    public sealed class NoopConventEvent : ContentEvent
    {
    }
}
