using Raf.Client.Core.Workspaces;
using Raf.Contracts;
using Raf.Domain.Entities;

namespace Raf.Client.Core.Tests;

/// <summary>Qué acciones ofrece la UI a cada rol. Debe coincidir con la matriz de permisos del Plan 00.</summary>
public class WorkspacePermissionsTests
{
    private static MemberDto Member(WorkspaceRole role) => new(Guid.NewGuid(), "x@test.local", "X", role);

    [Fact]
    public void Owner_can_do_everything_in_the_workspace_but_leave()
    {
        var p = new WorkspacePermissions(WorkspaceRole.Owner, IsAdmin: false);
        Assert.True(p.CanRun);
        Assert.True(p.CanEditContent);
        Assert.True(p.CanManageVariables);
        Assert.True(p.CanSeeSecrets);
        Assert.True(p.CanManageMembers);
        Assert.True(p.CanRenameWorkspace);
        Assert.True(p.CanDeleteWorkspace);
        Assert.True(p.CanTransferOwnership);
        Assert.False(p.CanLeave);
        Assert.False(p.CanAdministerServer);
    }

    [Fact]
    public void Editor_edits_content_and_secrets_but_does_not_manage_the_workspace()
    {
        var p = new WorkspacePermissions(WorkspaceRole.Editor, IsAdmin: false);
        Assert.True(p.CanRun);
        Assert.True(p.CanEditContent);
        Assert.True(p.CanManageVariables);
        Assert.True(p.CanSeeSecrets);
        Assert.False(p.CanManageMembers);
        Assert.False(p.CanRenameWorkspace);
        Assert.False(p.CanDeleteWorkspace);
        Assert.False(p.CanTransferOwnership);
        Assert.True(p.CanLeave);
    }

    [Fact]
    public void Viewer_only_reads_and_runs()
    {
        var p = new WorkspacePermissions(WorkspaceRole.Viewer, IsAdmin: false);
        Assert.True(p.CanRun);
        Assert.False(p.CanEditContent);
        Assert.False(p.CanManageVariables);
        Assert.False(p.CanSeeSecrets);
        Assert.False(p.CanManageMembers);
        Assert.False(p.CanRenameWorkspace);
        Assert.False(p.CanDeleteWorkspace);
        Assert.False(p.CanTransferOwnership);
        Assert.True(p.CanLeave);
        Assert.Equal("Viewer (solo lectura)", p.RoleText);
    }

    [Fact]
    public void Admin_role_does_not_grant_workspace_permissions()
    {
        var p = new WorkspacePermissions(WorkspaceRole.Viewer, IsAdmin: true);
        Assert.True(p.CanAdministerServer);
        Assert.False(p.CanEditContent);
        Assert.False(p.CanManageMembers);

        var outsider = WorkspacePermissions.For(null, isAdmin: true);
        Assert.False(outsider.IsMember);
        Assert.False(outsider.CanRun);
        Assert.False(outsider.CanLeave);
        Assert.True(outsider.CanAdministerServer);
    }

    [Theory]
    [InlineData(WorkspaceRole.Editor, true)]
    [InlineData(WorkspaceRole.Viewer, true)]
    [InlineData(WorkspaceRole.Owner, false)]
    public void Owner_manages_every_member_except_the_owner(WorkspaceRole memberRole, bool allowed)
    {
        var owner = new WorkspacePermissions(WorkspaceRole.Owner, false);
        var member = Member(memberRole);
        Assert.Equal(allowed, owner.CanChangeRoleOf(member));
        Assert.Equal(allowed, owner.CanRemove(member));
        Assert.Equal(allowed, owner.CanTransferTo(member));
    }

    [Theory]
    [InlineData(WorkspaceRole.Editor)]
    [InlineData(WorkspaceRole.Viewer)]
    public void Non_owners_cannot_touch_other_members(WorkspaceRole myRole)
    {
        var p = new WorkspacePermissions(myRole, false);
        var other = Member(WorkspaceRole.Viewer);
        Assert.False(p.CanChangeRoleOf(other));
        Assert.False(p.CanRemove(other));
        Assert.False(p.CanTransferTo(other));
    }

    [Fact]
    public void Permissions_follow_the_role_reported_by_the_workspace()
    {
        var ws = new WorkspaceDto(Guid.NewGuid(), "Equipo", WorkspaceRole.Editor, 1, 1);
        Assert.True(WorkspacePermissions.For(ws, false).CanEditContent);
        Assert.False(WorkspacePermissions.For(ws with { MyRole = WorkspaceRole.Viewer }, false).CanEditContent);
    }
}
