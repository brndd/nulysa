using Microsoft.EntityFrameworkCore;

namespace Nulysa.Data;

public static class QuartzSchema
{
    public static async Task Upgrade(AppDbContext db, CancellationToken ct = default)
    {
        if (!await db.Database.SqlQuery<bool>($"SELECT to_regclass('qrtz_job_details') IS NOT NULL AS \"Value\"").SingleAsync(ct)) return;
        if (!await db.Database.SqlQuery<bool>($"SELECT to_regclass('qrtz_job_status') IS NOT NULL AS \"Value\"").SingleAsync(ct))
            throw new InvalidOperationException("Quartz schemas older than 4.4 require the reviewed upstream migrations first.");
        // The three 4.6 columns and two indexes, from Quartz v4.6.2's Apache-2.0 migrations.
        await db.Database.ExecuteSqlRawAsync("""
            ALTER TABLE qrtz_fired_triggers ADD COLUMN IF NOT EXISTS interrupt_requested_time bigint NULL;
            ALTER TABLE qrtz_scheduler_state ADD COLUMN IF NOT EXISTS quartz_version varchar(20) NULL;
            ALTER TABLE qrtz_triggers ADD COLUMN IF NOT EXISTS misfire_threshold bigint NULL;
            CREATE INDEX IF NOT EXISTS idx_qrtz_eh_fire_inst ON qrtz_execution_history (sched_name, fire_instance_id);
            CREATE INDEX IF NOT EXISTS idx_qrtz_eh_trig_time ON qrtz_execution_history (sched_name, trigger_group, trigger_name, fired_time);
            """, ct);
    }
}
