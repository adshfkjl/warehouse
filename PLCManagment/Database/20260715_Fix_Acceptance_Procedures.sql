USE [DB_SDL1];
GO

ALTER PROCEDURE [dbo].[WX_GetTYByMO_NO]
    @MO_NO VARCHAR(20)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @IsGet VARCHAR(1),
            @BilId VARCHAR(2),
            @Pattern VARCHAR(20),
            @SequenceCount INT,
            @TI_NO VARCHAR(20),
            @TY_NO VARCHAR(20),
            @AffectedRows INT,
            @LockResult INT,
            @LockResource NVARCHAR(255);

    SET @MO_NO = LTRIM(RTRIM(ISNULL(@MO_NO, '')));
    IF @MO_NO = ''
    BEGIN
        RAISERROR(N'来源单号不能为空。', 16, 1);
        RETURN;
    END;

    SET @LockResource = N'WX_GetTYByMO_NO:' + @MO_NO;

    BEGIN TRY
        BEGIN TRANSACTION;

        EXEC @LockResult = sys.sp_getapplock
            @Resource = @LockResource,
            @LockMode = 'Exclusive',
            @LockOwner = 'Transaction',
            @LockTimeout = 10000;

        IF @LockResult < 0
            RAISERROR(N'验收单正在被其他请求处理，请稍后重试。', 16, 1);

        IF LEFT(@MO_NO, 2) = 'TI'
        BEGIN
            SET @IsGet = 'F';
            SET @BilId = 'TY';
            SET @Pattern = @BilId + FORMAT(GETDATE(), 'yyMM') + 'NNNN';
            SET @SequenceCount = 0;

            SELECT TOP (1) @TY_NO = TY_NO
            FROM TF_TY WITH (UPDLOCK, HOLDLOCK)
            WHERE TY_ID = 'TY' AND TI_NO = @MO_NO
            ORDER BY TY_NO DESC;

            IF @TY_NO IS NULL
            BEGIN
                EXEC SQNO_GetCnt @IsGet, @BilId, @Pattern, @SequenceCount OUTPUT;
                SET @TY_NO = LEFT(@Pattern, 6) + RIGHT('0000' + CAST(@SequenceCount AS VARCHAR(10)), 4);

                INSERT INTO MF_TY(
                    TY_ID, TY_NO, TY_DD, SAL_NO, CUS_NO, TI_NO, USR, CHK_MAN, PRT_SW,
                    CLS_DATE, BIL_TYPE, DEP, CUS_OS_NO, MOB_ID, SYS_DATE, CLS_ID_OK,
                    CLS_ID_LOST, PRT_USR, CNTT_NO, CHK_KND
                )
                SELECT TOP (1)
                    'TY', @TY_NO, CONVERT(VARCHAR(10), GETDATE(), 120), '04.139', ti.CUS_NO,
                    ti.TI_NO, '0703', '0703', 'N', CONVERT(VARCHAR(10), GETDATE(), 120),
                    '01', '07', '', '', CONVERT(VARCHAR(19), GETDATE(), 120), 'F', 'F', '', '', 1
                FROM MF_TI ti
                WHERE ti.TI_ID = 'TI' AND ti.TI_NO = @MO_NO;

                SET @AffectedRows = @@ROWCOUNT;
                IF @AffectedRows <> 1
                    RAISERROR(N'找不到唯一的送检单表头，无法创建验收单。', 16, 1);

                INSERT INTO TF_TY(
                    TY_ID, TY_NO, ITM, PRD_NO, PRD_NAME, PRD_MARK, WH, UNIT, QTY_CHK,
                    QTY_OK, BIL_NO, TI_NO, BAT_NO, SPC_NO, PRC_ID, EST_ITM, PRE_ITM,
                    QTY1_CHK, QTY1_OK, FREE_ID, VALID_DD, SL_NO, TI_ITM, SH_NO_CUS,
                    CAS_NO, GF_NO, CUS_OS_NO, RK_DD, DEP_RK, CNTT_NO, AMT_DIS_CNT
                )
                SELECT
                    'TY', @TY_NO, ti.ITM, ti.PRD_NO, ti.PRD_NAME, ti.PRD_MARK, ti.WH,
                    ti.UNIT, ti.QTY, ti.QTY, ti.BIL_NO, ti.TI_NO, ti.BAT_NO, '', '',
                    ti.EST_ITM, ti.ITM, ti.QTY1, ti.QTY1, ti.FREE_ID, ti.VALID_DD,
                    ti.SL_NO, ti.PRE_ITM, ti.SH_NO_CUS, ti.CAS_NO, ti.GF_NO,
                    ti.CUS_OS_NO, ti.RK_DD, ti.DEP_RK, ti.CNTT_NO, 0
                FROM TF_TI ti
                WHERE ti.TI_ID = 'TI' AND ti.TI_NO = @MO_NO;

                SET @AffectedRows = @@ROWCOUNT;
                IF @AffectedRows = 0
                    RAISERROR(N'送检单没有表身，无法创建验收单。', 16, 1);

                UPDATE TF_TI
                SET QTY_RK = QTY, QTY_RTN = QTY
                WHERE TI_ID = 'TI' AND TI_NO = @MO_NO;

                UPDATE MF_TI
                SET CLOSE_ID = 'T'
                WHERE TI_ID = 'TI' AND TI_NO = @MO_NO;
            END
            ELSE IF NOT EXISTS (
                SELECT 1 FROM MF_TY WHERE TY_ID = 'TY' AND TY_NO = @TY_NO
            )
            BEGIN
                INSERT INTO MF_TY(
                    TY_ID, TY_NO, TY_DD, SAL_NO, CUS_NO, TI_NO, USR, CHK_MAN, PRT_SW,
                    CLS_DATE, BIL_TYPE, DEP, CUS_OS_NO, MOB_ID, SYS_DATE, CLS_ID_OK,
                    CLS_ID_LOST, PRT_USR, CNTT_NO, CHK_KND
                )
                SELECT TOP (1)
                    'TY', @TY_NO, CONVERT(VARCHAR(10), GETDATE(), 120), '04.139', ti.CUS_NO,
                    ti.TI_NO, '0703', '0703', 'N', CONVERT(VARCHAR(10), GETDATE(), 120),
                    '01', '07', '', '', CONVERT(VARCHAR(19), GETDATE(), 120), 'F', 'F', '', '', 1
                FROM MF_TI ti
                WHERE ti.TI_ID = 'TI' AND ti.TI_NO = @MO_NO;

                IF @@ROWCOUNT <> 1
                    RAISERROR(N'验收单表头缺失，且无法从送检单恢复。', 16, 1);
            END;

            SELECT
                b.TY_ID, b.TY_NO, b.ITM, b.PRD_NO, p.NAME, b.PRD_NAME, p.SPC, p.UT,
                b.QTY_CHK, b.QTY_OK, b.BIL_NO, b.TI_NO, b.SPC_NO, b.PRC_ID, b.QTY_LOST,
                b.QTY_OK_RTN, a.CLS_ID_OK, a.CLS_ID_LOST, a.CHK_KND,
                CASE WHEN b.MM_NO IS NULL
                           AND ISNULL(b.QTY_OK_RTN, 0) + ISNULL(b.QTY_LOST_RTN, 0) = 0
                     THEN 0 ELSE 1 END AS STAT
            FROM MF_TY a
            INNER JOIN TF_TY b ON a.TY_ID = b.TY_ID AND a.TY_NO = b.TY_NO
            INNER JOIN PRDT p ON b.PRD_NO = p.PRD_NO
            WHERE b.TY_ID = 'TY' AND b.TI_NO = @MO_NO;
        END
        ELSE IF LEFT(@MO_NO, 2) = 'MO'
        BEGIN
            SET @IsGet = 'F';
            SET @BilId = 'T6';
            SET @Pattern = @BilId + FORMAT(GETDATE(), 'yyMM') + 'NNNN';
            SET @SequenceCount = 0;

            SELECT TOP (1) @TY_NO = TY_NO, @TI_NO = TI_NO
            FROM TF_TY WITH (UPDLOCK, HOLDLOCK)
            WHERE TY_ID = 'TP' AND BIL_NO = @MO_NO
            ORDER BY TY_NO DESC;

            IF @TI_NO IS NULL
            BEGIN
                SELECT TOP (1) @TI_NO = TI_NO
                FROM TF_TI WITH (UPDLOCK, HOLDLOCK)
                WHERE TI_ID = 'T6' AND BIL_ID = 'MO' AND BIL_NO = @MO_NO
                ORDER BY TI_NO DESC;
            END;

            IF @TI_NO IS NULL
            BEGIN
                EXEC SQNO_GetCnt @IsGet, @BilId, @Pattern, @SequenceCount OUTPUT;
                SET @TI_NO = LEFT(@Pattern, 6) + RIGHT('0000' + CAST(@SequenceCount AS VARCHAR(10)), 4);

                INSERT INTO MF_TI(
                    TI_ID, TI_NO, BIL_TYPE, TI_DD, BIL_ID, BIL_NO, SAL_NO, CLOSE_ID,
                    CUS_NO, DEP, BAT_NO, USR, CHK_MAN, PRT_SW, CLS_DATE, OS_ID, OS_NO,
                    CHKTY_ID, MOB_ID, SYS_DATE
                )
                SELECT
                    'T6', @TI_NO, '01', CONVERT(VARCHAR(10), GETDATE(), 120), 'MO',
                    mo.MO_NO, '', 'F', mo.CUS_NO, mo.DEP, mo.BAT_NO, '0403', '0403',
                    'Y', CONVERT(VARCHAR(10), GETDATE(), 120), 'MO', mo.MO_NO, 'T', '',
                    CONVERT(VARCHAR(19), GETDATE(), 120)
                FROM MF_MO mo
                WHERE mo.MO_NO = @MO_NO AND mo.QTY > ISNULL(mo.QTY_RK, 0);

                IF @@ROWCOUNT <> 1
                    RAISERROR(N'制令单不存在、已全部送检或存在重复记录，无法创建送检单。', 16, 1);

                INSERT INTO TF_TI(
                    TI_ID, TI_NO, ITM, PRD_NO, PRD_NAME, PRD_MARK, WH, UNIT, QTY,
                    BIL_ID, BIL_NO, BAT_NO, EST_ITM, QTY_RK, ID_NO, ZC_NO, QTY1,
                    FREE_ID, CK_ITM, SUP_PRD_NO, CUS_OS_NO, VALID_DD, PRE_ITM,
                    CAS_NO, DEP_RK, CNTT_NO, SUP_PRD_MARK
                )
                SELECT
                    'T6', @TI_NO, 1, mo.MRP_NO, p.NAME, mo.PRD_MARK, mo.WH, mo.UNIT,
                    mo.QTY - ISNULL(mo.QTY_RK, 0), 'MO', @MO_NO, '', 1, mo.QTY,
                    mo.ID_NO, '', mo.QTY1, '', 1, ISNULL(mo.SUP_PRD_NO, ''),
                    ISNULL(mo.CUS_OS_NO, ''), '1899-12-30', 1, '', '', '',
                    ISNULL(mo.SUP_PRD_MARK, '')
                FROM MF_MO mo
                INNER JOIN PRDT p ON mo.MRP_NO = p.PRD_NO
                WHERE mo.MO_NO = @MO_NO AND mo.QTY > ISNULL(mo.QTY_RK, 0);

                IF @@ROWCOUNT = 0
                    RAISERROR(N'制令单没有可送检的产品明细。', 16, 1);

                UPDATE MF_MO SET QTY_RK = QTY WHERE MO_NO = @MO_NO;
            END;

            IF @TY_NO IS NULL
            BEGIN
                SET @IsGet = 'F';
                SET @BilId = 'TP';
                SET @Pattern = @BilId + FORMAT(GETDATE(), 'yyMM') + 'NNNN';
                SET @SequenceCount = 0;

                EXEC SQNO_GetCnt @IsGet, @BilId, @Pattern, @SequenceCount OUTPUT;
                SET @TY_NO = LEFT(@Pattern, 6) + RIGHT('0000' + CAST(@SequenceCount AS VARCHAR(10)), 4);

                INSERT INTO MF_TY(
                    TY_ID, TY_NO, TY_DD, SAL_NO, TI_NO, USR, CHK_MAN, PRT_SW,
                    CLS_DATE, DEP, MOB_ID, SYS_DATE, PRT_USR, CHK_KND, BIL_TYPE
                )
                SELECT
                    'TP', @TY_NO, CONVERT(VARCHAR(10), GETDATE(), 120), '', ti.TI_NO,
                    '0703', '0703', 'N', CONVERT(VARCHAR(10), GETDATE(), 120), '07', '',
                    CONVERT(VARCHAR(19), GETDATE(), 120), '', 1, '01'
                FROM MF_TI ti
                WHERE ti.TI_ID = 'T6' AND ti.TI_NO = @TI_NO;

                IF @@ROWCOUNT <> 1
                    RAISERROR(N'找不到本次送检单表头，无法创建验收单。', 16, 1);

                INSERT INTO TF_TY(
                    TY_ID, TY_NO, ITM, PRD_NO, PRD_NAME, PRD_MARK, WH, UNIT, QTY_CHK,
                    QTY_OK, BIL_NO, TI_NO, BAT_NO, SPC_NO, PRC_ID, EST_ITM, PRE_ITM,
                    ID_NO, QTY1_CHK, QTY1_OK, VALID_DD, TI_ITM, CAS_NO, USED_TIME,
                    CUS_OS_NO, RK_DD, DEP_RK, CNTT_NO, REM
                )
                SELECT
                    'TP', @TY_NO, ti.ITM, ti.PRD_NO, ti.PRD_NAME, ti.PRD_MARK, ti.WH,
                    ti.UNIT, ti.QTY, ti.QTY, ti.BIL_NO, ti.TI_NO, ti.BAT_NO, '', '',
                    1, 1, ti.ID_NO, ti.QTY1, ti.QTY1, ti.VALID_DD, ti.PRE_ITM, '', 0,
                    ti.CUS_OS_NO, ti.VALID_DD, ti.DEP_RK, ti.CNTT_NO, ti.REM
                FROM TF_TI ti
                WHERE ti.TI_ID = 'T6' AND ti.TI_NO = @TI_NO;

                IF @@ROWCOUNT = 0
                    RAISERROR(N'本次送检单没有表身，无法创建验收单。', 16, 1);

                UPDATE TF_TI
                SET QTY_RK = QTY, QTY_RTN = QTY
                WHERE TI_ID = 'T6' AND TI_NO = @TI_NO;

                UPDATE MF_TI
                SET CLOSE_ID = 'T'
                WHERE TI_ID = 'T6' AND TI_NO = @TI_NO;

                UPDATE MF_MO SET QTY_CHK = QTY WHERE MO_NO = @MO_NO;
            END
            ELSE IF NOT EXISTS (
                SELECT 1 FROM MF_TY WHERE TY_ID = 'TP' AND TY_NO = @TY_NO
            )
            BEGIN
                INSERT INTO MF_TY(
                    TY_ID, TY_NO, TY_DD, SAL_NO, TI_NO, USR, CHK_MAN, PRT_SW,
                    CLS_DATE, DEP, MOB_ID, SYS_DATE, PRT_USR, CHK_KND, BIL_TYPE
                )
                SELECT
                    'TP', @TY_NO, CONVERT(VARCHAR(10), GETDATE(), 120), '', ti.TI_NO,
                    '0703', '0703', 'N', CONVERT(VARCHAR(10), GETDATE(), 120), '07', '',
                    CONVERT(VARCHAR(19), GETDATE(), 120), '', 1, '01'
                FROM MF_TI ti
                WHERE ti.TI_ID = 'T6' AND ti.TI_NO = @TI_NO;

                IF @@ROWCOUNT <> 1
                    RAISERROR(N'验收单表头缺失，且无法从对应送检单恢复。', 16, 1);
            END;

            SELECT
                b.TY_ID, b.TY_NO, b.ITM, b.PRD_NO, p.NAME, b.PRD_NAME, p.SPC, p.UT,
                b.QTY_CHK, b.QTY_OK, b.BIL_NO, b.TI_NO, b.SPC_NO, b.PRC_ID, b.QTY_LOST,
                b.QTY_OK_RTN, a.CLS_ID_OK, a.CLS_ID_LOST, a.CHK_KND,
                CASE WHEN b.MM_NO IS NULL
                           AND ISNULL(b.QTY_OK_RTN, 0) + ISNULL(b.QTY_LOST_RTN, 0) = 0
                     THEN 0 ELSE 1 END AS STAT
            FROM MF_TY a
            INNER JOIN TF_TY b ON a.TY_ID = b.TY_ID AND a.TY_NO = b.TY_NO
            INNER JOIN PRDT p ON b.PRD_NO = p.PRD_NO
            WHERE b.TY_ID = 'TP' AND b.BIL_NO = @MO_NO;
        END
        ELSE IF LEFT(@MO_NO, 2) = 'T7'
        BEGIN
            SET @IsGet = 'F';
            SET @BilId = 'TO';
            SET @Pattern = @BilId + FORMAT(GETDATE(), 'yyMM') + 'NNNN';
            SET @SequenceCount = 0;

            SELECT TOP (1) @TY_NO = TY_NO
            FROM TF_TY WITH (UPDLOCK, HOLDLOCK)
            WHERE TY_ID = 'TO' AND TI_NO = @MO_NO
            ORDER BY TY_NO DESC;

            IF @TY_NO IS NULL
            BEGIN
                EXEC SQNO_GetCnt @IsGet, @BilId, @Pattern, @SequenceCount OUTPUT;
                SET @TY_NO = LEFT(@Pattern, 6) + RIGHT('0000' + CAST(@SequenceCount AS VARCHAR(10)), 4);

                INSERT INTO MF_TY(
                    TY_ID, TY_NO, TY_DD, SAL_NO, CUS_NO, TI_NO, USR, CHK_MAN, PRT_SW,
                    CLS_DATE, BIL_TYPE, DEP, CUS_OS_NO, MOB_ID, SYS_DATE, CLS_ID_OK,
                    CLS_ID_LOST, PRT_USR, CNTT_NO, CHK_KND
                )
                SELECT TOP (1)
                    'TO', @TY_NO, CONVERT(VARCHAR(10), GETDATE(), 120), 'SDL2535', ti.CUS_NO,
                    ti.TI_NO, '0703', '0703', 'N', CONVERT(VARCHAR(10), GETDATE(), 120),
                    '01', '04', '', '', CONVERT(VARCHAR(19), GETDATE(), 120), 'F', 'F', '', '', 1
                FROM MF_TI ti
                WHERE ti.TI_ID = 'T7' AND ti.TI_NO = @MO_NO;

                IF @@ROWCOUNT <> 1
                    RAISERROR(N'找不到唯一的委外送检单表头，无法创建验收单。', 16, 1);

                INSERT INTO TF_TY(
                    TY_ID, TY_NO, ITM, PRD_NO, PRD_NAME, PRD_MARK, WH, UNIT, QTY_CHK,
                    QTY_OK, BIL_NO, TI_NO, BAT_NO, SPC_NO, MO_NO, PRC_ID, EST_ITM,
                    PRE_ITM, ID_NO, QC_UP, QTY1_CHK, QTY1_OK, FREE_ID, VALID_DD,
                    SL_NO, TI_ITM, SH_NO_CUS, MM_ID, CAS_NO, CUS_OS_NO, RK_DD,
                    DEP_RK, AMT_DIS_CNT
                )
                SELECT
                    'TO', @TY_NO, ti.ITM, ti.PRD_NO, ti.PRD_NAME, ti.PRD_MARK, ti.WH,
                    ti.UNIT, ti.QTY, ti.QTY, ti.BIL_NO, ti.TI_NO, ti.BAT_NO, '', '', '',
                    ti.EST_ITM, ti.ITM, ti.ID_NO, 0, ti.QTY1, ti.QTY1, ti.FREE_ID,
                    ti.VALID_DD, ti.SL_NO, ti.PRE_ITM, ti.SH_NO_CUS, 'TB', ti.CAS_NO,
                    ti.CUS_OS_NO, ti.RK_DD, ti.DEP_RK, 0
                FROM TF_TI ti
                WHERE ti.TI_ID = 'T7' AND ti.TI_NO = @MO_NO;

                IF @@ROWCOUNT = 0
                    RAISERROR(N'委外送检单没有表身，无法创建验收单。', 16, 1);

                UPDATE TF_TI
                SET QTY_RK = QTY, QTY_RTN = QTY
                WHERE TI_ID = 'T7' AND TI_NO = @MO_NO;

                UPDATE MF_TI
                SET CLOSE_ID = 'T'
                WHERE TI_ID = 'T7' AND TI_NO = @MO_NO;
            END
            ELSE IF NOT EXISTS (
                SELECT 1 FROM MF_TY WHERE TY_ID = 'TO' AND TY_NO = @TY_NO
            )
            BEGIN
                INSERT INTO MF_TY(
                    TY_ID, TY_NO, TY_DD, SAL_NO, CUS_NO, TI_NO, USR, CHK_MAN, PRT_SW,
                    CLS_DATE, BIL_TYPE, DEP, CUS_OS_NO, MOB_ID, SYS_DATE, CLS_ID_OK,
                    CLS_ID_LOST, PRT_USR, CNTT_NO, CHK_KND
                )
                SELECT TOP (1)
                    'TO', @TY_NO, CONVERT(VARCHAR(10), GETDATE(), 120), 'SDL2535', ti.CUS_NO,
                    ti.TI_NO, '0703', '0703', 'N', CONVERT(VARCHAR(10), GETDATE(), 120),
                    '01', '04', '', '', CONVERT(VARCHAR(19), GETDATE(), 120), 'F', 'F', '', '', 1
                FROM MF_TI ti
                WHERE ti.TI_ID = 'T7' AND ti.TI_NO = @MO_NO;

                IF @@ROWCOUNT <> 1
                    RAISERROR(N'委外验收单表头缺失，且无法从送检单恢复。', 16, 1);
            END;

            SELECT
                b.TY_ID, b.TY_NO, b.ITM, b.PRD_NO, p.NAME, b.PRD_NAME, p.SPC, p.UT,
                b.QTY_CHK, b.QTY_OK, b.BIL_NO, b.TI_NO, b.SPC_NO, b.PRC_ID, b.QTY_LOST,
                b.QTY_OK_RTN, a.CLS_ID_OK, a.CLS_ID_LOST, a.CHK_KND,
                CASE WHEN b.MM_NO IS NULL
                           AND ISNULL(b.QTY_OK_RTN, 0) + ISNULL(b.QTY_LOST_RTN, 0) = 0
                     THEN 0 ELSE 1 END AS STAT
            FROM MF_TY a
            INNER JOIN TF_TY b ON a.TY_ID = b.TY_ID AND a.TY_NO = b.TY_NO
            INNER JOIN PRDT p ON b.PRD_NO = p.PRD_NO
            WHERE b.TY_ID = 'TO' AND b.TI_NO = @MO_NO;
        END
        ELSE
        BEGIN
            RAISERROR(N'不支持的来源单号类型。', 16, 1);
        END;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
GO

ALTER PROCEDURE [dbo].[WX_SaveTYInfo]
    @TY_ID VARCHAR(2),
    @TY_NO VARCHAR(20),
    @ITM INT,
    @MO_NO VARCHAR(20),
    @QTY_OK NUMERIC(22, 2),
    @QTY_LOST NUMERIC(22, 2),
    @SPC_NO VARCHAR(10),
    @PRC_ID VARCHAR(1),
    @REM VARCHAR(600),
    @RES INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @ActualMoNo VARCHAR(20),
            @SourceTiNo VARCHAR(20),
            @Found BIT,
            @DocumentDeleted BIT,
            @LockResult INT,
            @LockResource NVARCHAR(255);

    SET @RES = 0;
    SET @Found = 0;
    SET @DocumentDeleted = 0;
    SET @TY_ID = LTRIM(RTRIM(ISNULL(@TY_ID, '')));
    SET @TY_NO = LTRIM(RTRIM(ISNULL(@TY_NO, '')));
    SET @MO_NO = LTRIM(RTRIM(ISNULL(@MO_NO, '')));

    IF @TY_ID = '' OR @TY_NO = '' OR @ITM <= 0
    BEGIN
        RAISERROR(N'验收单号或项次无效。', 16, 1);
        RETURN;
    END;

    SET @LockResource = N'WX_SaveTYInfo:' + @TY_ID + N':' + @TY_NO;

    BEGIN TRY
        BEGIN TRANSACTION;

        EXEC @LockResult = sys.sp_getapplock
            @Resource = @LockResource,
            @LockMode = 'Exclusive',
            @LockOwner = 'Transaction',
            @LockTimeout = 10000;

        IF @LockResult < 0
            RAISERROR(N'验收单正在被其他请求处理，请稍后重试。', 16, 1);

        SELECT TOP (1)
            @Found = 1,
            @ActualMoNo = BIL_NO,
            @SourceTiNo = TI_NO
        FROM TF_TY WITH (UPDLOCK, HOLDLOCK)
        WHERE TY_ID = @TY_ID AND TY_NO = @TY_NO AND ITM = @ITM;

        IF @Found = 0
            RAISERROR(N'指定的验收单明细不存在。', 16, 1);

        IF ISNULL(@ActualMoNo, '') <> ''
            SET @MO_NO = @ActualMoNo;

        IF EXISTS (
            SELECT 1
            FROM TF_TY b
            WHERE b.TY_ID = @TY_ID
              AND b.TY_NO = @TY_NO
              AND b.ITM = @ITM
              AND (
                    b.MM_NO IS NOT NULL
                    OR ISNULL(b.QTY_OK_RTN, 0) <> 0
                    OR ISNULL(b.QTY_LOST_RTN, 0) <> 0
                  )
        )
        BEGIN
            SET @RES = 1;
        END
        ELSE IF @REM = '##del##'
        BEGIN
            DELETE FROM TF_TY
            WHERE TY_ID = @TY_ID AND TY_NO = @TY_NO AND ITM = @ITM;

            IF @@ROWCOUNT <> 1
                RAISERROR(N'验收单明细删除失败。', 16, 1);

            IF NOT EXISTS (
                SELECT 1 FROM TF_TY WHERE TY_ID = @TY_ID AND TY_NO = @TY_NO
            )
            BEGIN
                DELETE FROM MF_TY
                WHERE TY_ID = @TY_ID AND TY_NO = @TY_NO;

                SET @DocumentDeleted = 1;

                IF @TY_ID = 'TP' AND ISNULL(@SourceTiNo, '') <> ''
                BEGIN
                    DELETE FROM TF_TI
                    WHERE TI_ID = 'T6'
                      AND TI_NO = @SourceTiNo
                      AND NOT EXISTS (
                          SELECT 1 FROM TF_TY
                          WHERE TY_ID = 'TP' AND TI_NO = @SourceTiNo
                      );

                    DELETE FROM MF_TI
                    WHERE TI_ID = 'T6'
                      AND TI_NO = @SourceTiNo
                      AND NOT EXISTS (
                          SELECT 1 FROM TF_TI
                          WHERE TI_ID = 'T6' AND TI_NO = @SourceTiNo
                      );
                END;

                IF @TY_ID = 'TP'
                   AND ISNULL(@MO_NO, '') <> ''
                   AND NOT EXISTS (
                       SELECT 1 FROM TF_TY
                       WHERE TY_ID = 'TP' AND BIL_NO = @MO_NO
                   )
                BEGIN
                    UPDATE MF_MO
                    SET QTY_CHK = 0, QTY_LOST = 0, QTY_RK = 0
                    WHERE MO_NO = @MO_NO;
                END;
            END;
        END
        ELSE
        BEGIN
            UPDATE TF_TY
            SET QTY_OK = @QTY_OK,
                QTY_LOST = @QTY_LOST,
                SPC_NO = @SPC_NO,
                PRC_ID = @PRC_ID,
                REM = @REM
            WHERE TY_ID = @TY_ID AND TY_NO = @TY_NO AND ITM = @ITM;

            IF @@ROWCOUNT <> 1
                RAISERROR(N'验收单明细更新失败。', 16, 1);

            IF @TY_ID = 'TP' AND ISNULL(@MO_NO, '') <> ''
            BEGIN
                UPDATE MF_MO
                SET QTY_CHK = @QTY_OK,
                    QTY_LOST = @QTY_LOST
                WHERE MO_NO = @MO_NO;
            END;
        END;

        COMMIT TRANSACTION;

        IF @REM <> '##del##' OR @RES = 1
        BEGIN
            SELECT
                b.TY_ID, b.TY_NO, b.ITM, b.PRD_NO, p.NAME, b.PRD_NAME, p.SPC, p.UT,
                b.QTY_CHK, b.QTY_OK, b.BIL_NO, b.TI_NO, b.SPC_NO, b.PRC_ID, b.QTY_LOST,
                b.QTY_OK_RTN, a.CLS_ID_OK, a.CLS_ID_LOST, a.CHK_KND,
                CASE WHEN b.MM_NO IS NULL
                           AND ISNULL(b.QTY_OK_RTN, 0) + ISNULL(b.QTY_LOST_RTN, 0) = 0
                     THEN 0 ELSE 1 END AS STAT
            FROM MF_TY a
            INNER JOIN TF_TY b ON a.TY_ID = b.TY_ID AND a.TY_NO = b.TY_NO
            INNER JOIN PRDT p ON b.PRD_NO = p.PRD_NO
            WHERE b.TY_ID = @TY_ID AND b.TY_NO = @TY_NO;
        END
        ELSE
        BEGIN
            SELECT
                'TP' AS TY_ID,
                '' AS TY_NO,
                0 AS ITM,
                '' AS PRD_NO,
                '' AS NAME,
                '' AS PRD_NAME,
                '' AS SPC,
                '' AS UT,
                CAST(0 AS NUMERIC(22, 2)) AS QTY_CHK,
                CAST(0 AS NUMERIC(22, 2)) AS QTY_OK,
                '' AS BIL_NO,
                '' AS TI_NO,
                '' AS SPC_NO,
                '' AS PRC_ID,
                CAST(0 AS NUMERIC(22, 2)) AS QTY_LOST,
                CAST(NULL AS NUMERIC(22, 2)) AS QTY_OK_RTN,
                '' AS CLS_ID_OK,
                '' AS CLS_ID_LOST,
                0 AS CHK_KND,
                1 AS STAT;
        END;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
GO
