INSERT INTO [dbo].[stage_contoso_Incident] (
[contoso_caseid],
[contoso_incidentid],
[contoso_legacyid],
[contoso_narrative])
SELECT lk1.[contoso_caseid],
glt.[contoso_incidentid],
m.[legacyid],
m.[narrative]
FROM [Legacy].[dbo].[Incident] m
LEFT JOIN [dbo].[guid_contoso_Case] AS lk1 ON lk1.[contoso_legacyid] = m.[caseid]
LEFT JOIN [dbo].[guid_contoso_Incident] as glt on glt.[contoso_legacyid] = m.[legacyid] 
OUTER APPLY
(SELECT TOP(1) DeltaDate FROM [dbo].[UpdateTime] WHERE [rowid] = 1) AS ud
WHERE m.[UpdateDate] >= ud.[DeltaDate] OR m.[CreateDate] >= ud.[DeltaDate]
