using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TenantFlow.BuildingBlocks.Scheduling.Persistence.Migrations
{
    /// <summary>
    /// ADR-0023 decision 1: the scheduler schema, the application role's access to it, and Quartz.NET 3.22's PostgreSQL job
    /// store tables (database/tables/tables_postgres.sql of the 3.22.4 release, without its DROP block, in this schema).
    /// The Migrator runs this as the owner role; the Worker's Quartz uses the tables as the application role.
    /// </summary>
    [DbContext(typeof(SchedulerDbContext))]
    [Migration("20261021160000_QuartzTables")]
    public partial class QuartzTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(name: "scheduler");
            migrationBuilder.Sql("GRANT USAGE ON SCHEMA scheduler TO tenantflow_app;");
            migrationBuilder.Sql("ALTER DEFAULT PRIVILEGES IN SCHEMA scheduler GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO tenantflow_app;");
            migrationBuilder.Sql("ALTER DEFAULT PRIVILEGES IN SCHEMA scheduler GRANT USAGE, SELECT ON SEQUENCES TO tenantflow_app;");
            migrationBuilder.Sql("""
                CREATE TABLE scheduler.qrtz_job_details (
                    sched_name TEXT NOT NULL, job_name TEXT NOT NULL, job_group TEXT NOT NULL, description TEXT NULL,
                    job_class_name TEXT NOT NULL, is_durable BOOL NOT NULL, is_nonconcurrent BOOL NOT NULL, is_update_data BOOL NOT NULL,
                    requests_recovery BOOL NOT NULL, job_data BYTEA NULL,
                    PRIMARY KEY (sched_name, job_name, job_group));

                CREATE TABLE scheduler.qrtz_triggers (
                    sched_name TEXT NOT NULL, trigger_name TEXT NOT NULL, trigger_group TEXT NOT NULL, job_name TEXT NOT NULL,
                    job_group TEXT NOT NULL, description TEXT NULL, next_fire_time BIGINT NULL, prev_fire_time BIGINT NULL,
                    priority INTEGER NULL, trigger_state TEXT NOT NULL, trigger_type TEXT NOT NULL, start_time BIGINT NOT NULL,
                    end_time BIGINT NULL, calendar_name TEXT NULL, misfire_instr SMALLINT NULL, misfire_orig_fire_time BIGINT NULL,
                    execution_group VARCHAR(200) NULL, preferred_node VARCHAR(200) NULL, preferred_node_auto BOOL NOT NULL DEFAULT FALSE,
                    job_data BYTEA NULL,
                    PRIMARY KEY (sched_name, trigger_name, trigger_group),
                    FOREIGN KEY (sched_name, job_name, job_group) REFERENCES scheduler.qrtz_job_details (sched_name, job_name, job_group));

                CREATE TABLE scheduler.qrtz_simple_triggers (
                    sched_name TEXT NOT NULL, trigger_name TEXT NOT NULL, trigger_group TEXT NOT NULL, repeat_count BIGINT NOT NULL,
                    repeat_interval BIGINT NOT NULL, times_triggered BIGINT NOT NULL,
                    PRIMARY KEY (sched_name, trigger_name, trigger_group),
                    FOREIGN KEY (sched_name, trigger_name, trigger_group) REFERENCES scheduler.qrtz_triggers (sched_name, trigger_name, trigger_group) ON DELETE CASCADE);

                CREATE TABLE scheduler.qrtz_simprop_triggers (
                    sched_name TEXT NOT NULL, trigger_name TEXT NOT NULL, trigger_group TEXT NOT NULL, str_prop_1 TEXT NULL,
                    str_prop_2 TEXT NULL, str_prop_3 TEXT NULL, int_prop_1 INTEGER NULL, int_prop_2 INTEGER NULL, long_prop_1 BIGINT NULL,
                    long_prop_2 BIGINT NULL, dec_prop_1 NUMERIC NULL, dec_prop_2 NUMERIC NULL, bool_prop_1 BOOL NULL, bool_prop_2 BOOL NULL,
                    time_zone_id TEXT NULL,
                    PRIMARY KEY (sched_name, trigger_name, trigger_group),
                    FOREIGN KEY (sched_name, trigger_name, trigger_group) REFERENCES scheduler.qrtz_triggers (sched_name, trigger_name, trigger_group) ON DELETE CASCADE);

                CREATE TABLE scheduler.qrtz_cron_triggers (
                    sched_name TEXT NOT NULL, trigger_name TEXT NOT NULL, trigger_group TEXT NOT NULL, cron_expression TEXT NOT NULL,
                    time_zone_id TEXT,
                    PRIMARY KEY (sched_name, trigger_name, trigger_group),
                    FOREIGN KEY (sched_name, trigger_name, trigger_group) REFERENCES scheduler.qrtz_triggers (sched_name, trigger_name, trigger_group) ON DELETE CASCADE);

                CREATE TABLE scheduler.qrtz_blob_triggers (
                    sched_name TEXT NOT NULL, trigger_name TEXT NOT NULL, trigger_group TEXT NOT NULL, blob_data BYTEA NULL,
                    PRIMARY KEY (sched_name, trigger_name, trigger_group),
                    FOREIGN KEY (sched_name, trigger_name, trigger_group) REFERENCES scheduler.qrtz_triggers (sched_name, trigger_name, trigger_group) ON DELETE CASCADE);

                CREATE TABLE scheduler.qrtz_calendars (
                    sched_name TEXT NOT NULL, calendar_name TEXT NOT NULL, calendar BYTEA NOT NULL,
                    PRIMARY KEY (sched_name, calendar_name));

                CREATE TABLE scheduler.qrtz_paused_trigger_grps (
                    sched_name TEXT NOT NULL, trigger_group TEXT NOT NULL,
                    PRIMARY KEY (sched_name, trigger_group));

                CREATE TABLE scheduler.qrtz_fired_triggers (
                    sched_name TEXT NOT NULL, entry_id TEXT NOT NULL, trigger_name TEXT NOT NULL, trigger_group TEXT NOT NULL,
                    instance_name TEXT NOT NULL, fired_time BIGINT NOT NULL, sched_time BIGINT NOT NULL, priority INTEGER NOT NULL,
                    state TEXT NOT NULL, job_name TEXT NULL, job_group TEXT NULL, is_nonconcurrent BOOL NOT NULL, requests_recovery BOOL NULL,
                    execution_group VARCHAR(200) NULL,
                    PRIMARY KEY (sched_name, entry_id));

                CREATE TABLE scheduler.qrtz_scheduler_state (
                    sched_name TEXT NOT NULL, instance_name TEXT NOT NULL, last_checkin_time BIGINT NOT NULL, checkin_interval BIGINT NOT NULL,
                    PRIMARY KEY (sched_name, instance_name));

                CREATE TABLE scheduler.qrtz_locks (
                    sched_name TEXT NOT NULL, lock_name TEXT NOT NULL,
                    PRIMARY KEY (sched_name, lock_name));

                CREATE INDEX idx_qrtz_j_req_recovery ON scheduler.qrtz_job_details (sched_name, requests_recovery);
                CREATE INDEX idx_qrtz_j_g_n ON scheduler.qrtz_job_details (sched_name, job_group, job_name);
                CREATE INDEX idx_qrtz_t_j ON scheduler.qrtz_triggers (sched_name, job_name, job_group);
                CREATE INDEX idx_qrtz_t_c ON scheduler.qrtz_triggers (sched_name, calendar_name);
                CREATE INDEX idx_qrtz_t_g_n ON scheduler.qrtz_triggers (sched_name, trigger_group, trigger_name);
                CREATE INDEX idx_qrtz_t_next_fire_time ON scheduler.qrtz_triggers (sched_name, next_fire_time);
                CREATE INDEX idx_qrtz_t_nft_st ON scheduler.qrtz_triggers (sched_name, trigger_state, next_fire_time);
                CREATE INDEX idx_qrtz_ft_inst_job_req_rcvry ON scheduler.qrtz_fired_triggers (sched_name, instance_name, requests_recovery);
                CREATE INDEX idx_qrtz_ft_j_g ON scheduler.qrtz_fired_triggers (sched_name, job_name, job_group);
                CREATE INDEX idx_qrtz_ft_t_g ON scheduler.qrtz_fired_triggers (sched_name, trigger_name, trigger_group);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in new[] { "fired_triggers", "paused_trigger_grps", "scheduler_state", "locks", "simprop_triggers", "simple_triggers", "cron_triggers", "blob_triggers", "triggers", "job_details", "calendars" })
            {
                migrationBuilder.Sql($"DROP TABLE scheduler.qrtz_{table};");
            }

            migrationBuilder.Sql("ALTER DEFAULT PRIVILEGES IN SCHEMA scheduler REVOKE USAGE, SELECT ON SEQUENCES FROM tenantflow_app;");
            migrationBuilder.Sql("ALTER DEFAULT PRIVILEGES IN SCHEMA scheduler REVOKE SELECT, INSERT, UPDATE, DELETE ON TABLES FROM tenantflow_app;");
            migrationBuilder.Sql("REVOKE USAGE ON SCHEMA scheduler FROM tenantflow_app;");
        }
    }
}
