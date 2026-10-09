INSERT INTO [dbo].[stage_contoso_Case] (
[contoso_bailamount],
[contoso_caseid],
[contoso_categoryid],
[contoso_courtid],
[contoso_filedon],
[contoso_incidentid],
[contoso_legacyid],
[contoso_priority],
[contoso_sealed])
SELECT m.[bailamount],
glt.[contoso_caseid],
lk1.[contoso_categoryid],
NULL,
m.[filedon],
lk2.[contoso_incidentid],
m.[legacyid],
m.[priority],
m.[sealed]
FROM [Legacy].[dbo].[Case] m
LEFT JOIN [dbo].[guid_contoso_Category] AS lk1 ON lk1.[contoso_legacyid] = m.[categoryid]
LEFT JOIN [dbo].[guid_contoso_Incident] AS lk2 ON lk2.[contoso_legacyid] = m.[incidentid]
LEFT JOIN [dbo].[guid_contoso_Case] as glt on glt.[contoso_legacyid] = m.[legacyid] 
OUTER APPLY
(SELECT TOP(1) DeltaDate FROM [dbo].[UpdateTime] WHERE [rowid] = 1) AS ud
WHERE m.[UpdateDate] >= ud.[DeltaDate] OR m.[CreateDate] >= ud.[DeltaDate]
