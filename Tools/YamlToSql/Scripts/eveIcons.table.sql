﻿SET ANSI_NULLS ON

SET QUOTED_IDENTIFIER ON

SET ANSI_PADDING ON

CREATE TABLE [dbo].[eveIcons](
	[iconID] [int] NOT NULL,
	[iconFile] [varchar](500) NOT NULL,
	[description] [nvarchar](max) NOT NULL,
 CONSTRAINT [eveIcons_PK] PRIMARY KEY CLUSTERED 
(
	[iconID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]


SET ANSI_PADDING OFF

ALTER TABLE [dbo].[eveIcons] ADD  DEFAULT ('') FOR [iconFile]

ALTER TABLE [dbo].[eveIcons] ADD  DEFAULT ('') FOR [description]