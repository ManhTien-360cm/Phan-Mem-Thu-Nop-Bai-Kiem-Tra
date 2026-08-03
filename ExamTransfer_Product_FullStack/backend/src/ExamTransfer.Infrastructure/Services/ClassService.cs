using System.Text;
using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure.Importing;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Shared.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System.Security.Claims;

namespace ExamTransfer.Infrastructure.Services;

public sealed class ClassService(AppDbContext db, IMemoryCache cache, IAuditService audit, IOutboxService outbox, ICloudAdapter? cloud = null, IHttpContextAccessor? httpContextAccessor = null) : IClassService
{
    public async Task<PagedResult<ClassSummaryDto>> ListAsync(string? search, int page, int pageSize, CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var query = db.ClassesSet.AsNoTracking()
            .Where(x => x.Status != ClassStatus.Archived);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(x => x.Name.Contains(term) || x.Code.Contains(term) || x.SchoolYear.Contains(term));
        }

        var total = await query.CountAsync(cancellationToken);
        var sortKeys = await query
            .Select(x => new { x.Id, x.UpdatedAtUtc })
            .ToListAsync(cancellationToken);
        var pageIds = sortKeys
            .OrderByDescending(x => x.UpdatedAtUtc)
            .ThenByDescending(x => x.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => x.Id)
            .ToList();

        if (pageIds.Count == 0)
            return new PagedResult<ClassSummaryDto>([], page, pageSize, total);

        var position = pageIds
            .Select((id, index) => new { id, index })
            .ToDictionary(x => x.id, x => x.index);
        var rows = await query
            .Where(x => pageIds.Contains(x.Id))
            .Select(x => new { Entity = x, StudentCount = x.Members.Count })
            .ToListAsync(cancellationToken);
        var items = rows
            .OrderBy(x => position[x.Entity.Id])
            .Select(x => x.Entity.ToSummary(x.StudentCount))
            .ToList();

        return new PagedResult<ClassSummaryDto>(items, page, pageSize, total);
    }

    public async Task<ClassDetailDto> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var entity = await db.ClassesSet.AsNoTracking().Include(x => x.Members).FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy lớp.", 404);
        var requestRows = await db.ClassEnrollmentRequestsSet.AsNoTracking()
            .Where(x => x.ClassId == id)
            .ToListAsync(cancellationToken);
        var requests = requestRows
            .OrderBy(x => x.Status == "Pending" ? 0 : 1)
            .ThenByDescending(x => x.RequestedAtUtc)
            .Select(ToEnrollmentDto)
            .ToList();
        return entity.ToDetail(entity.Members.OrderBy(x => x.StudentCode).Select(x => x.ToDto()).ToList())
            with { EnrollmentRequests = requests };
    }

    public async Task<ClassDetailDto> CreateAsync(CreateClassRequest request, CancellationToken cancellationToken)
    {
        return await InTransactionAsync(async () =>
        {
            ValidateClass(request.Name, request.Code, request.SchoolYear);
            if (await db.ClassesSet.AnyAsync(x => x.Code == request.Code.Trim() && x.SchoolYear == request.SchoolYear.Trim(), cancellationToken))
                throw new ApiException(ErrorCodes.Conflict, "Mã lớp đã tồn tại trong năm học này.", 409);
            var entity = new ClassRoom
            {
                Name = request.Name.Trim(), Code = request.Code.Trim(), SchoolYear = request.SchoolYear.Trim(),
                Description = request.Description?.Trim(), Status = ClassStatus.Active, AccessMode = request.AccessMode,
                CreatedBy = RequestActorId()
            };
            db.ClassesSet.Add(entity);
            await db.SaveChangesAsync(cancellationToken);
            await audit.WriteAsync("ClassCreated", nameof(ClassRoom), entity.Id.ToString(), null, null, entity, cancellationToken);
            await outbox.EnqueueAsync("classes", entity.Id.ToString(), "upsert", ToCloud(entity), cancellationToken: cancellationToken);
            return entity.ToDetail([]);
        }, cancellationToken);
    }

    public async Task<ClassDetailDto> UpdateAsync(Guid id, UpdateClassRequest request, CancellationToken cancellationToken)
    {
        return await InTransactionAsync(async () =>
        {
            ValidateClass(request.Name, request.Code, request.SchoolYear);
            var entity = await db.ClassesSet.Include(x => x.Members).FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
                ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy lớp.", 404);
            EnsureRowVersion(entity.RowVersion, request.RowVersion);
            if (await db.ClassesSet.AnyAsync(x => x.Id != id && x.Code == request.Code.Trim() && x.SchoolYear == request.SchoolYear.Trim(), cancellationToken))
                throw new ApiException(ErrorCodes.Conflict, "Mã lớp đã tồn tại trong năm học này.", 409);
            var before = new { entity.Name, entity.Code, entity.SchoolYear, entity.Description };
            entity.Name = request.Name.Trim();
            entity.Code = request.Code.Trim();
            entity.SchoolYear = request.SchoolYear.Trim();
            entity.Description = request.Description?.Trim();
            if (request.AccessMode.HasValue) entity.AccessMode = request.AccessMode.Value;
            await db.SaveChangesAsync(cancellationToken);
            await audit.WriteAsync("ClassUpdated", nameof(ClassRoom), entity.Id.ToString(), null, before, entity, cancellationToken);
            await outbox.EnqueueAsync("classes", entity.Id.ToString(), "upsert", ToCloud(entity), cancellationToken: cancellationToken);
            return entity.ToDetail(entity.Members.OrderBy(x => x.StudentCode).Select(x => x.ToDto()).ToList());
        }, cancellationToken);
    }

    public async Task ArchiveAsync(Guid id, CancellationToken cancellationToken)
    {
        _ = await BulkArchiveAsync(new([id]), cancellationToken);
    }

    public Task<BulkArchiveResultDto> BulkArchiveAsync(
        BulkArchiveRequest request,
        CancellationToken cancellationToken)
    {
        var ids = BulkArchiveValidation.Validate(request);
        return InTransactionAsync(async () =>
        {
            var entities = await db.ClassesSet
                .Where(x => ids.Contains(x.Id))
                .ToListAsync(cancellationToken);
            var found = entities.Select(x => x.Id).ToHashSet();
            var missing = ids.Where(id => !found.Contains(id)).ToList();
            if (missing.Count > 0)
                throw new ApiException(
                    ErrorCodes.NotFound,
                    "Một hoặc nhiều lớp không tồn tại.",
                    404,
                    details: new { missingIds = missing });

            var alreadyArchived = entities
                .Where(x => x.Status == ClassStatus.Archived)
                .Select(x => x.Id)
                .Order()
                .ToList();
            var toArchive = entities
                .Where(x => x.Status != ClassStatus.Archived)
                .OrderBy(x => x.Id)
                .ToList();

            foreach (var entity in toArchive)
                entity.Status = ClassStatus.Archived;
            await db.SaveChangesAsync(cancellationToken);

            foreach (var entity in toArchive)
            {
                await audit.WriteAsync(
                    "ClassArchived",
                    nameof(ClassRoom),
                    entity.Id.ToString(),
                    null,
                    new { status = ClassStatus.Active },
                    new { status = entity.Status },
                    cancellationToken);
                await outbox.EnqueueAsync(
                    "classes",
                    entity.Id.ToString(),
                    "upsert",
                    ToCloud(entity),
                    cancellationToken: cancellationToken);
            }

            return new BulkArchiveResultDto(
                ids.Count,
                toArchive.Count,
                alreadyArchived,
                []);
        }, cancellationToken);
    }

    public async Task<StudentDto> AddStudentAsync(Guid classId, CreateStudentRequest request, CancellationToken cancellationToken)
    {
        ValidateStudent(request.StudentCode, request.DisplayName);
        _ = await db.ClassesSet.FirstOrDefaultAsync(x => x.Id == classId, cancellationToken)
            ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy lớp.", 404);
        if (await db.ClassMembersSet.AnyAsync(x => x.ClassId == classId && x.StudentCode == request.StudentCode.Trim(), cancellationToken))
            throw new ApiException(ErrorCodes.DuplicateStudentCode, "Mã học sinh đã tồn tại trong lớp.", 409);
        var entity = new ClassMember
        {
            ClassId = classId, StudentCode = request.StudentCode.Trim(), DisplayName = request.DisplayName.Trim(),
            Email = request.Email?.Trim(), MetadataJson = request.MetadataJson
        };
        db.ClassMembersSet.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync("StudentAdded", nameof(ClassMember), entity.Id.ToString(), null, null,
            new { entity.Id, entity.ClassId, entity.UserId, entity.StudentCode, entity.DisplayName, entity.Email, entity.MetadataJson }, cancellationToken);
        await outbox.EnqueueAsync(
            "class_members",
            entity.Id.ToString(),
            "upsert",
            ToCloud(entity),
            cancellationToken: cancellationToken);
        return entity.ToDto();
    }

    public async Task<StudentDto> UpdateStudentAsync(Guid classId, Guid studentId, UpdateStudentRequest request, CancellationToken cancellationToken)
    {
        ValidateStudent(request.StudentCode, request.DisplayName);
        var entity = await db.ClassMembersSet.FirstOrDefaultAsync(x => x.Id == studentId && x.ClassId == classId, cancellationToken)
            ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy học sinh.", 404);
        if (await db.ClassMembersSet.AnyAsync(x => x.ClassId == classId && x.Id != studentId && x.StudentCode == request.StudentCode.Trim(), cancellationToken))
            throw new ApiException(ErrorCodes.DuplicateStudentCode, "Mã học sinh đã tồn tại trong lớp.", 409);
        var before = new { entity.StudentCode, entity.DisplayName, entity.Email, entity.MetadataJson };
        entity.StudentCode = request.StudentCode.Trim();
        entity.DisplayName = request.DisplayName.Trim();
        entity.Email = request.Email?.Trim();
        entity.MetadataJson = request.MetadataJson;
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync("StudentUpdated", nameof(ClassMember), entity.Id.ToString(), null, before, entity, cancellationToken);
        await outbox.EnqueueAsync(
            "class_members",
            entity.Id.ToString(),
            "upsert",
            ToCloud(entity),
            cancellationToken: cancellationToken);
        return entity.ToDto();
    }

    public async Task RemoveStudentAsync(Guid classId, Guid studentId, CancellationToken cancellationToken)
    {
        var entity = await db.ClassMembersSet.FirstOrDefaultAsync(x => x.Id == studentId && x.ClassId == classId, cancellationToken)
            ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy học sinh.", 404);
        db.ClassMembersSet.Remove(entity);
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync("StudentRemoved", nameof(ClassMember), entity.Id.ToString(), null, entity, null, cancellationToken);
        await outbox.EnqueueAsync(
            "class_members",
            entity.Id.ToString(),
            "delete",
            new { id = entity.Id },
            cancellationToken: cancellationToken);
    }

    public async Task<ImportPreviewDto> PreviewImportAsync(
        Guid classId,
        ImportPreviewRequest request,
        CancellationToken cancellationToken)
    {
        if (!await db.ClassesSet.AnyAsync(x => x.Id == classId, cancellationToken))
            throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy lớp.", 404);

        if (string.IsNullOrWhiteSpace(request.ContentBase64))
            throw new ApiException(ErrorCodes.ValidationFailed, "File import rỗng.");

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(request.ContentBase64);
        }
        catch (FormatException)
        {
            throw new ApiException(
                ErrorCodes.ValidationFailed,
                "Dữ liệu file import không phải Base64 hợp lệ.");
        }

        var worksheets = SpreadsheetImportReader.ReadWorksheets(request.FileName, bytes);
        if (worksheets.Count == 0)
            throw new ApiException(ErrorCodes.ValidationFailed, "File import không có dữ liệu.");

        List<List<string>>? rows = null;
        StudentImportHeaderMap? header = null;
        IReadOnlyList<string> observedHeaders = [];
        foreach (var worksheet in worksheets)
        {
            var candidate = StudentImportHeaderMapper.TryFindHeader(
                worksheet,
                requireDateOfBirth: false,
                out var observed,
                request.ColumnMapping);
            if (candidate is null)
            {
                observedHeaders = observedHeaders.Concat(observed).Take(30).ToList();
                continue;
            }

            rows = worksheet;
            header = candidate;
            break;
        }

        if (rows is null || header is null)
        {
            throw new ApiException(
                ErrorCodes.ValidationFailed,
                "Không tìm thấy hàng tiêu đề có cột mã học sinh và họ tên.",
                details: new
                {
                    expected = "Mã sinh viên/Mã học sinh và Họ và tên hoặc Họ đệm + Tên",
                    observedHeaders
                });
        }

        var candidates = new List<ImportCandidate>();
        var errors = new List<ImportRowErrorDto>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = header.RowIndex + 1; index < rows.Count; index++)
        {
            var sourceRowNumber = index + 1;
            var row = rows[index];
            var code = StudentImportHeaderMap
                .ReadCell(row, header.StudentCodeColumn)
                .Trim();
            var name = header.ReadDisplayName(row);
            var email = header.EmailColumn is { } emailColumn
                ? StudentImportHeaderMap.ReadCell(row, emailColumn).Trim()
                : null;

            var rowHasError = false;
            if (string.IsNullOrWhiteSpace(code))
            {
                errors.Add(new(
                    sourceRowNumber,
                    "studentCode",
                    ErrorCodes.ValidationFailed,
                    "Thiếu mã học sinh."));
                rowHasError = true;
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                errors.Add(new(
                    sourceRowNumber,
                    "displayName",
                    ErrorCodes.ValidationFailed,
                    "Thiếu họ tên."));
                rowHasError = true;
            }

            if (!string.IsNullOrWhiteSpace(code) && !seen.Add(code))
            {
                errors.Add(new(
                    sourceRowNumber,
                    "studentCode",
                    ErrorCodes.DuplicateStudentCode,
                    "Trùng mã trong file."));
                rowHasError = true;
            }

            candidates.Add(new ImportCandidate(
                sourceRowNumber,
                new StudentDto(
                    Guid.NewGuid(),
                    code,
                    name,
                    string.IsNullOrWhiteSpace(email) ? null : email,
                    null),
                rowHasError));
        }

        var token = Guid.NewGuid().ToString("N");
        cache.Set(
            $"class-import:{token}",
            new ImportCache(classId, candidates, errors),
            TimeSpan.FromMinutes(30));

        var validRows = candidates.Count(x => !x.HasError);
        return new ImportPreviewDto(
            token,
            candidates.Count,
            validRows,
            candidates.Count - validRows,
            candidates.Where(x => !x.HasError).Select(x => x.Student).Take(100).ToList(),
            errors);
    }

    public async Task<ImportCommitResultDto> CommitImportAsync(
        Guid classId,
        ImportCommitRequest request,
        CancellationToken cancellationToken)
    {
        if (!cache.TryGetValue<ImportCache>(
                $"class-import:{request.PreviewToken}",
                out var preview)
            || preview is null
            || preview.ClassId != classId)
        {
            throw new ApiException(
                ErrorCodes.TransferExpired,
                "Preview token đã hết hạn hoặc không thuộc lớp này.",
                410);
        }

        if (preview.Candidates.Any(x => x.HasError) && !request.SkipInvalidRows)
        {
            throw new ApiException(
                ErrorCodes.ValidationFailed,
                "File còn dòng lỗi; hãy sửa hoặc bật bỏ qua dòng lỗi.");
        }

        var existing = await db.ClassMembersSet
            .Where(x => x.ClassId == classId)
            .Select(x => x.StudentCode)
            .ToListAsync(cancellationToken);
        var existingSet = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var duplicateErrors = new List<ImportRowErrorDto>();
        var toInsert = new List<ClassMember>();

        foreach (var candidate in preview.Candidates)
        {
            if (candidate.HasError)
                continue;

            if (!existingSet.Add(candidate.Student.StudentCode))
            {
                duplicateErrors.Add(new ImportRowErrorDto(
                    candidate.RowNumber,
                    "studentCode",
                    ErrorCodes.DuplicateStudentCode,
                    "Mã học sinh đã tồn tại trong lớp."));
                continue;
            }

            toInsert.Add(new ClassMember
            {
                ClassId = classId,
                StudentCode = candidate.Student.StudentCode,
                DisplayName = candidate.Student.DisplayName,
                Email = candidate.Student.Email,
                MetadataJson = candidate.Student.MetadataJson
            });
        }

        db.ClassMembersSet.AddRange(toInsert);
        await db.SaveChangesAsync(cancellationToken);

        foreach (var member in toInsert)
        {
            await outbox.EnqueueAsync(
                "class_members",
                member.Id.ToString(),
                "upsert",
                ToCloud(member),
                cancellationToken: cancellationToken);
        }

        cache.Remove($"class-import:{request.PreviewToken}");
        var combinedErrors = preview.Errors.Concat(duplicateErrors).ToList();
        var skipped = preview.Candidates.Count - toInsert.Count;

        await audit.WriteAsync(
            "ClassImportCommitted",
            nameof(ClassRoom),
            classId.ToString(),
            null,
            null,
            new { inserted = toInsert.Count, skipped },
            cancellationToken);

        return new ImportCommitResultDto(
            toInsert.Count,
            skipped,
            combinedErrors);
    }

    public async Task<byte[]> ExportCsvAsync(Guid classId, CancellationToken cancellationToken)
    {
        var rows = await db.ClassMembersSet.AsNoTracking().Where(x => x.ClassId == classId).OrderBy(x => x.StudentCode).ToListAsync(cancellationToken);
        var sb = new StringBuilder();
        sb.AppendLine("studentCode,displayName,email");
        foreach (var row in rows) sb.AppendLine($"{SpreadsheetImportReader.EscapeCsv(row.StudentCode)},{SpreadsheetImportReader.EscapeCsv(row.DisplayName)},{SpreadsheetImportReader.EscapeCsv(row.Email ?? string.Empty)}");
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    public async Task<IReadOnlyList<ClassEnrollmentRequestDto>> ListEnrollmentRequestsAsync(
        Guid classId,
        CancellationToken cancellationToken)
    {
        if (!await db.ClassesSet.AnyAsync(x => x.Id == classId, cancellationToken))
            throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy lớp.", 404);
        var rows = await db.ClassEnrollmentRequestsSet.AsNoTracking()
            .Where(x => x.ClassId == classId)
            .ToListAsync(cancellationToken);
        return rows
            .OrderBy(x => x.Status == "Pending" ? 0 : 1)
            .ThenByDescending(x => x.RequestedAtUtc)
            .Select(ToEnrollmentDto)
            .ToList();
    }

    public async Task<ClassEnrollmentRequestDto> ApproveEnrollmentAsync(
        Guid classId,
        Guid requestId,
        Guid mutationRequestId,
        CancellationToken cancellationToken)
    {
        if (mutationRequestId == Guid.Empty) throw new ApiException(ErrorCodes.ValidationFailed, "Thiếu MutationRequestId.");
        var local = await FindEnrollmentAsync(classId, requestId, cancellationToken);
        var result = await RequireCloud().ApprovePublicEnrollmentAsync(
            requestId,
            mutationRequestId,
            cancellationToken);
        return ToEnrollmentDto(local) with
        {
            Status = result.Status,
            DecidedAtUtc = result.DecidedAtUtc,
            CloudVersion = result.CloudVersion
        };
    }

    public async Task<ClassEnrollmentRequestDto> RejectEnrollmentAsync(
        Guid classId,
        Guid requestId,
        string? reason,
        Guid mutationRequestId,
        CancellationToken cancellationToken)
    {
        if (mutationRequestId == Guid.Empty) throw new ApiException(ErrorCodes.ValidationFailed, "Thiếu MutationRequestId.");
        var local = await FindEnrollmentAsync(classId, requestId, cancellationToken);
        var result = await RequireCloud().RejectPublicEnrollmentAsync(
            requestId,
            reason,
            mutationRequestId,
            cancellationToken);
        return ToEnrollmentDto(local) with
        {
            Status = result.Status,
            DecidedAtUtc = result.DecidedAtUtc,
            CloudVersion = result.CloudVersion
        };
    }

    private static object ToCloud(ClassRoom x) => new
    {
        id = x.Id,
        name = x.Name,
        code = x.Code,
        school_year = x.SchoolYear,
        description = x.Description,
        status = x.Status.ToString(),
        access_mode = x.AccessMode.ToString(),
        enrollment_open = x.EnrollmentOpen,
        require_enrollment_approval = x.RequireEnrollmentApproval,
        enrollment_code_hash = x.EnrollmentCodeHash,
        enrollment_opened_at = x.EnrollmentOpenedAtUtc,
        enrollment_closed_at = x.EnrollmentClosedAtUtc,
        public_version = x.PublicVersion,
        created_by = x.CreatedBy,
        created_at = x.CreatedAtUtc,
        updated_at = x.UpdatedAtUtc
    };

    private async Task<ClassEnrollmentRequest> FindEnrollmentAsync(
        Guid classId,
        Guid requestId,
        CancellationToken cancellationToken) =>
        await db.ClassEnrollmentRequestsSet.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == requestId && x.ClassId == classId, cancellationToken)
        ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy yêu cầu ghi danh.", 404);

    private ICloudAdapter RequireCloud() =>
        cloud ?? throw new ApiException(
            ErrorCodes.CloudOffline,
            "PublicCloud chưa được cấu hình cho thao tác ghi danh.",
            503);

    private Guid? RequestActorId() =>
        Guid.TryParse(
            httpContextAccessor?.HttpContext?.User.FindFirstValue("provider_user_id")
                ?? httpContextAccessor?.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? httpContextAccessor?.HttpContext?.User.FindFirstValue("sub"),
            out var actorId)
            && actorId != Guid.Empty
                ? actorId
                : null;

    private static ClassEnrollmentRequestDto ToEnrollmentDto(ClassEnrollmentRequest x) =>
        new(
            x.Id,
            x.ClassId,
            x.StudentUserId,
            x.StudentCode,
            x.Status,
            x.RequestedAtUtc,
            x.DecidedAtUtc,
            x.CloudVersion);

    private async Task<T> InTransactionAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        await using var transaction = await db.BeginTransactionAsync(cancellationToken);
        try
        {
            var result = await action();
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
    private static object ToCloud(ClassMember x) => new
    {
        id = x.Id,
        class_id = x.ClassId,
        user_id = x.UserId,
        student_code = x.StudentCode,
        display_name = x.DisplayName,
        email = x.Email,
        metadata_json = x.MetadataJson,
        created_at = x.CreatedAtUtc,
        updated_at = x.UpdatedAtUtc
    };
    private static void ValidateClass(string name, string code, string year)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(year))
            throw new ApiException(ErrorCodes.ValidationFailed, "Tên lớp, mã lớp và năm học là bắt buộc.");
    }
    private static void ValidateStudent(string code, string name)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name))
            throw new ApiException(ErrorCodes.ValidationFailed, "Mã học sinh và họ tên là bắt buộc.");
    }
    private static void EnsureRowVersion(string current, string supplied)
    {
        if (!string.Equals(current, supplied, StringComparison.Ordinal)) throw new ApiException(ErrorCodes.ConcurrencyConflict, "Dữ liệu đã được người khác cập nhật.", 409, details: new { currentRowVersion = current });
    }
    private sealed record ImportCandidate(
        int RowNumber,
        StudentDto Student,
        bool HasError);

    private sealed record ImportCache(
        Guid ClassId,
        IReadOnlyList<ImportCandidate> Candidates,
        IReadOnlyList<ImportRowErrorDto> Errors);


}
