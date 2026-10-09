INSERT INTO [dbo].[stage_contoso_Category] (
[contoso_activedate],
[contoso_categorycode],
[contoso_categoryid],
[contoso_expirationdate],
[contoso_formid],
[contoso_legacyid])
SELECT m.[activedate],
m.[categorycode],
glt.[contoso_categoryid],
m.[expirationdate],
m.[formid],
m.[legacyid]
FROM [Legacy].[dbo].[Category] m 
LEFT JOIN [dbo].[guid_contoso_Category] as glt on glt.[contoso_legacyid] = m.[legacyid] 
OUTER APPLY
(SELECT TOP(1) DeltaDate FROM [dbo].[UpdateTime] WHERE [rowid] = 1) AS ud
WHERE m.[UpdateDate] >= ud.[DeltaDate] OR m.[CreateDate] >= ud.[DeltaDate]