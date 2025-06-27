﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Registry.Web.Identity.Models;
using Registry.Web.Models;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Ports;

public interface IUsersManager
{
    Task<AuthenticateResponse> Authenticate(string userName, string password);
    Task<AuthenticateResponse> Authenticate(string token);
    Task<IEnumerable<UserDto>> GetAll();
    Task<IEnumerable<UserDetailDto>> GetAllDetailed();
    Task<UserDto> CreateUser(string userName, string email, string password, string[] roles);
    Task DeleteUser(string userName);
    Task<ChangePasswordResult> ChangePassword(string userName, string currentPassword, string newPassword);
    Task<ChangePasswordResult> ChangePassword(string currentPassword, string newPassword);
    Task<AuthenticateResponse> Refresh();
    Task<UserStorageInfo> GetUserStorageInfo(string userName = null);
    Task<Dictionary<string, object>> GetUserMeta(string userName = null);
    Task SetUserMeta(string userId, Dictionary<string, object> meta);
    Task<string[]> GetRoles();
    Task CreateRole(string roleName);
    Task DeleteRole(string roleName);
    Task UpdateUserRoles(string userName, string[] roles);
    Task UpdateUser(string userName, string email);
    Task<OrganizationDto[]> GetUserOrganizations(string userName);
    Task SetUserOrganizations(string userName, string[] orgSlugs);
}